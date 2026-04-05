using System.IO;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Dakis ingest pipeline. FileSystemWatcher triggers on new folders,
/// debounce gives Dakis time to finish writing, then parse + insert + verify.
/// Timer fallback catches anything the watcher missed.
/// </summary>
public class DakisIngestService : IDisposable
{
    public DakisOrderParser Parser => _parser;
    private readonly DakisOrderParser _parser;
    private readonly IngestOrderWriter _writer;
    private readonly Data.Repositories.IOrderRepository _orders;
    private readonly AppSettings _settings;

    private FileSystemWatcher? _watcher;

    public DakisIngestService(
        DakisOrderParser parser,
        IngestOrderWriter writer,
        Data.Repositories.IOrderRepository orders,
        AppSettings settings)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Start watching the Dakis folder for new order directories.
    /// Call once at startup after settings are loaded.
    /// </summary>
    public void StartWatching()
    {
        StopWatching();

        if (!_settings.DakisEnabled) return;

        var watchFolder = _settings.DakisWatchFolder;
        if (string.IsNullOrEmpty(watchFolder) || !Directory.Exists(watchFolder)) return;

        // Watch for download_complete marker files in order subdirectories
        _watcher = new FileSystemWatcher(watchFolder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName,
            Filter = IngestConstants.MarkerDownloadComplete
        };
        _watcher.Created += OnMarkerCreated;
        _watcher.EnableRaisingEvents = true;

        AppLog.Info($"Dakis watcher started: {watchFolder}");
    }

    public void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    /// <summary>
    /// Triggered when download_complete marker appears in an order's metadata folder.
    /// Path: {watchFolder}/{order folder}/metadata/download_complete
    /// Walk up to the order folder and process it.
    /// </summary>
    private void OnMarkerCreated(object sender, FileSystemEventArgs e)
    {
        try
        {
            // e.FullPath = .../order 61005406/metadata/download_complete
            var metadataDir = Path.GetDirectoryName(e.FullPath);
            if (metadataDir == null) return;
            var orderFolder = Path.GetDirectoryName(metadataDir);
            if (orderFolder == null || !Directory.Exists(orderFolder)) return;

            ProcessOrderFolder(orderFolder);
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Parsing,
                $"Failed to process Dakis folder from watcher: {e.FullPath}",
                ex: ex);
        }
    }

    /// <summary>
    /// Full scan of the Dakis watch folder. Timer fallback in case watcher misses an event.
    /// </summary>
    public void ScanFolder()
    {
        if (!_settings.DakisEnabled) return;

        var watchFolder = _settings.DakisWatchFolder;
        if (string.IsNullOrEmpty(watchFolder) || !Directory.Exists(watchFolder))
            return;

        foreach (var orderDir in Directory.GetDirectories(watchFolder))
        {
            // Dakis order folders always start with "order " — skip anything else
            var folderName = Path.GetFileName(orderDir);
            if (!folderName.StartsWith("order ", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                ProcessOrderFolder(orderDir);
            }
            catch (Exception ex)
            {
                AlertCollector.Error(AlertCategory.Parsing,
                    $"Failed to process Dakis folder: {Path.GetFileName(orderDir)}",
                    orderId: Path.GetFileName(orderDir), ex: ex);
            }
        }
    }

    private void ProcessOrderFolder(string folderPath)
    {
        // Check for download_complete marker — Dakis is still writing if absent
        if (!IngestConstants.MarkerExists(folderPath, IngestConstants.MarkerDownloadComplete))
            return;

        var orderId = Path.GetFileName(folderPath);
        IngestOrder(orderId, folderPath);
    }

    /// <summary>
    /// Parse and ingest a Dakis order from disk. Handles multi-fulfiller splits.
    /// Called by both the file watcher and Verify (single code path for all Dakis ingest).
    /// </summary>
    public void IngestOrder(string orderId, string folderPath)
    {
        var ymlPath = Path.Combine(folderPath, "order.yml");
        if (!File.Exists(ymlPath))
        {
            AlertCollector.Error(AlertCategory.Parsing,
                $"order.yml not found in {orderId}",
                orderId: orderId);
            return;
        }

        var ymlContent = File.ReadAllText(ymlPath);

        var raw = new RawOrder(
            ExternalOrderId: orderId,
            SourceName: "dakis",
            RawData: ymlContent,
            Metadata: new Dictionary<string, string> { ["folder_path"] = folderPath });

        var order = _parser.Parse(raw);

        // File verification happens in verify after insert — not here
        order = order with { DownloadStatus = IngestConstants.StatusReady };

        // Resolve pickup store from Dakis billing_store ID (e.g. "881" → store 1)
        AppLog.Info($"Dakis ingest {order.ExternalOrderId}: billing='{order.BillingStoreId}' resolving...");
        var pickupStoreId = _orders.ResolveStoreId("dakis", order.BillingStoreId ?? "")
                            ?? _settings.StoreId;

        if (order.IsMultiFulfiller)
        {
            ProcessMultiFulfillerOrder(order, pickupStoreId, folderPath);
        }
        else
        {
            _writer.WriteToSqlite(order, pickupStoreId, "dakis", order.FolderPath ?? "", _settings.StoreId);
        }
    }

    /// <summary>
    /// Handle Dakis orders split across multiple stores.
    /// Creates a parent order (no items, tracking hub) + a child order with this store's items.
    /// Second store finds the parent via sync and adds its own child.
    /// </summary>
    private void ProcessMultiFulfillerOrder(UnifiedOrder order, int pickupStoreId, string folderPath)
    {
        var externalOrderId = order.ExternalOrderId;
        var storeCode = _orders.GetStoreName(_settings.StoreId); // "BH" or "WB"

        // Every multi-fulfiller order has a parent — ensure it exists and is marked display_tab=3
        var parentId = EnsureParentOrder(order, pickupStoreId, folderPath);
        if (parentId != null)
            _orders.SetDisplayTab(parentId, 3);

        // Separate local items (this store produces) from remote items
        var localItems = order.Items.Where(i => i.IsLocalProduction).ToList();

        if (localItems.Count == 0)
        {
            AppLog.Info($"Dakis multi-fulfiller {externalOrderId}: invoice-only at {storeCode}, no child created");
            return;
        }

        // Check if this store already has a child for this order (idempotent re-ingest)
        var existingChildId = FindExistingStoreChild(externalOrderId, storeCode);
        if (existingChildId != null)
        {
            AppLog.Info($"Dakis multi-fulfiller {externalOrderId}: {storeCode} child already exists, skipping");
            return;
        }

        // Generate child external_order_id: "12345-BH1"
        var childExternalId = GenerateChildExternalId(externalOrderId, storeCode);

        // Create child order with only local items
        var childOrder = order with
        {
            ExternalOrderId = childExternalId,
            Items = localItems
        };

        _writer.WriteToSqlite(childOrder, pickupStoreId, "dakis", order.FolderPath ?? "", _settings.StoreId);

        // Look up the child's internal ID (just inserted)
        var childId = _orders.FindOrderIdAnyStore(childExternalId);
        if (childId == null)
        {
            AlertCollector.Error(AlertCategory.Database,
                $"Dakis split: child order {childExternalId} not found after insert",
                orderId: childExternalId,
                detail: $"Attempted: find child after WriteToSqlite. Expected: order ID. " +
                        $"Found: null. Context: parent {externalOrderId}, store {storeCode}. " +
                        $"State: order_link not created.");
            return;
        }

        // Link child to parent
        if (parentId != null)
        {
            _orders.InsertLink(parentId, childId, "dakis_split", "ingest");
            AppLog.Info($"Dakis multi-fulfiller {externalOrderId}: created child {childExternalId} linked to parent");
        }
    }

    /// <summary>
    /// Ensure the parent order exists for a multi-fulfiller Dakis order.
    /// Parent has customer info but no items — it's a tracking hub.
    /// Returns the parent's internal order ID.
    /// </summary>
    private string? EnsureParentOrder(UnifiedOrder order, int pickupStoreId, string folderPath)
    {
        var parentId = _orders.FindOrderIdAnyStore(order.ExternalOrderId);
        if (parentId != null)
            return parentId;

        // Create parent with no items
        var parentOrder = order with
        {
            Items = [],
            IsInvoiceOnly = true // allows zero items through validation
        };

        _writer.WriteToSqlite(parentOrder, pickupStoreId, "dakis", order.FolderPath ?? "", _settings.StoreId);

        parentId = _orders.FindOrderIdAnyStore(order.ExternalOrderId);
        if (parentId == null)
        {
            AlertCollector.Error(AlertCategory.Database,
                $"Dakis split: parent order {order.ExternalOrderId} not found after insert",
                orderId: order.ExternalOrderId,
                detail: $"Attempted: find parent after WriteToSqlite. Expected: order ID. " +
                        $"Found: null. Context: folder {folderPath}. " +
                        $"State: child will be created without link.");
        }

        return parentId;
    }

    /// <summary>
    /// Check if this store already created a child for this parent order.
    /// Returns the child's internal ID if found, null otherwise.
    /// </summary>
    private string? FindExistingStoreChild(string parentExternalId, string storeCode)
    {
        return _orders.FindOrderIdByPattern($"{parentExternalId}-{storeCode}%");
    }

    /// <summary>
    /// Generate the next child external_order_id for a store.
    /// Pattern: "12345-BH1", "12345-BH2", etc.
    /// </summary>
    private string GenerateChildExternalId(string parentExternalId, string storeCode)
    {
        int seq = 1;
        while (_orders.FindOrderIdAnyStore($"{parentExternalId}-{storeCode}{seq}") != null)
            seq++;

        return $"{parentExternalId}-{storeCode}{seq}";
    }

    public void Dispose()
    {
        StopWatching();
        GC.SuppressFinalize(this);
    }
}
