using System.IO;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Dakis ingest pipeline. Follows the flow:
/// 1. Watch incoming folder for new order folders
/// 2. Check for metadata/download_complete marker (Dakis still writing if absent)
/// 3. Parse order.yml
/// 4. Verify files (exists, >1KB, JPEG magic bytes)
/// 5. Write to SQLite — insert new or compare-and-repair existing
/// </summary>
public class DakisIngestService
{
    private readonly DakisOrderParser _parser;
    private readonly IOrderRepository _orders;
    private readonly IHistoryRepository _history;
    private readonly AppSettings _settings;

    public DakisIngestService(
        DakisOrderParser parser,
        IOrderRepository orders,
        IHistoryRepository history,
        AppSettings settings)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Scan the Dakis watch folder for new/updated orders.
    /// Called on a timer or triggered by FileSystemWatcher.
    /// </summary>
    public void ScanFolder()
    {
        if (!_settings.DakisEnabled) return;

        var watchFolder = _settings.DakisWatchFolder;
        if (string.IsNullOrEmpty(watchFolder) || !Directory.Exists(watchFolder))
            return;

        foreach (var orderDir in Directory.GetDirectories(watchFolder))
        {
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
        // Step 2: Check for download_complete marker
        if (!IngestConstants.MarkerExists(folderPath, IngestConstants.MarkerDownloadComplete))
            return; // Dakis is still writing

        var orderId = Path.GetFileName(folderPath);

        // Read order.yml
        var ymlPath = Path.Combine(folderPath, "order.yml");
        if (!File.Exists(ymlPath))
        {
            AlertCollector.Warn(AlertCategory.Parsing,
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

        // Step 3: Parse
        var order = _parser.Parse(raw);

        // Step 4: Verify files
        var errors = new List<string>();
        foreach (var item in order.Items)
        {
            if (string.IsNullOrEmpty(item.ImageFilepath)) continue;
            if (!item.IsNoritsu) continue; // Non-Noritsu items don't need files checked here

            var error = OrderHelpers.VerifyFile(item.ImageFilepath);
            if (error != null)
                errors.Add($"{item.ImageFilename}: {error}");
        }

        if (errors.Count > 0)
        {
            order = order with
            {
                DownloadStatus = IngestConstants.StatusDownloadError,
                DownloadErrors = errors
            };
            AlertCollector.Warn(AlertCategory.DataQuality,
                $"Dakis order {orderId}: {errors.Count} file(s) failed verification",
                orderId: orderId);
        }
        else
        {
            order = order with { DownloadStatus = IngestConstants.StatusReady };
        }

        // Step 5: Write to SQLite
        WriteToSqlite(order);
    }

    private void WriteToSqlite(UnifiedOrder order)
    {
        var existingId = _orders.FindOrderId(order.ExternalOrderId, _settings.StoreId);

        if (existingId == null)
        {
            var orderId = _orders.InsertOrder(order, _settings.StoreId);
            _history.AddNote(orderId, $"Order received at {DateTime.Now:g}");
            AppLog.Info($"Inserted Dakis order {order.ExternalOrderId} (id={orderId}, {order.Items.Count} items)");
        }
        else
        {
            // TODO: compare-and-repair
            AppLog.Info($"Dakis order {order.ExternalOrderId} already exists (id={existingId})");
        }
    }
}
