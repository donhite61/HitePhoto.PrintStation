using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Core.Decisions;
using HitePhoto.PrintStation.Core.Ingest;
using HitePhoto.PrintStation.Core.Models;
using HitePhoto.PrintStation.Core.Services;
using HitePhoto.PrintStation.Data;
using HitePhoto.PrintStation.Data.Repositories;
using HitePhoto.Shared.Models;
using Microsoft.Data.Sqlite;

namespace HitePhoto.PrintStation.UI.ViewModels;

/// <summary>
/// Owns all data and logic for the main window.
/// MainWindow binds to this — it should never query the database directly.
/// </summary>
public class MainViewModel : ViewModelBase
{
    private readonly OrderDb _db;
    private readonly IOrderRepository _orders;
    private readonly IHistoryRepository _history;
    private readonly IHoldDecision _holdDecision;
    private readonly IHoldService _holdService;
    private readonly IChannelDecision _channelDecision;
    private readonly IFilesNeededDecision _filesNeededDecision;
    private readonly PixfizzIngestService _pixfizzIngest;
    private readonly DakisIngestService _dakisIngest;
    private readonly DakisOrderParser _dakisParser;
    private readonly AppSettings _settings;

    // ── Observable collections for tree views ──
    public ObservableCollection<OrderTreeItem> PendingOrders { get; } = new();
    public ObservableCollection<OrderTreeItem> PrintedOrders { get; } = new();
    public ObservableCollection<OrderTreeItem> OtherStoreOrders { get; } = new();

    // ── Selected state ──
    private OrderTreeItem? _selectedOrder;
    public OrderTreeItem? SelectedOrder
    {
        get => _selectedOrder;
        set { SetField(ref _selectedOrder, value); OnOrderSelected(); }
    }

    // ── Filter/sort ──
    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { SetField(ref _searchText, value); }
    }

    private string _sourceFilter = "All";
    public string SourceFilter
    {
        get => _sourceFilter;
        set { SetField(ref _sourceFilter, value); }
    }

    private string _sortMode = "Date Received";
    public string SortMode
    {
        get => _sortMode;
        set { SetField(ref _sortMode, value); }
    }

    // ── Status ──
    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    /// <summary>Set by background tasks when new data is available. UI thread checks this to decide whether to refresh.</summary>
    public volatile bool NeedsRefresh;

    private bool _isDbConnected;
    public bool IsDbConnected
    {
        get => _isDbConnected;
        set => SetField(ref _isDbConnected, value);
    }

    // ── Notes for selected order ──
    public ObservableCollection<HistoryEntry> OrderNotes { get; } = new();

    public MainViewModel(
        OrderDb db,
        IOrderRepository orders,
        IHistoryRepository history,
        IHoldDecision holdDecision,
        IHoldService holdService,
        IChannelDecision channelDecision,
        IFilesNeededDecision filesNeededDecision,
        PixfizzIngestService pixfizzIngest,
        DakisIngestService dakisIngest,
        DakisOrderParser dakisParser,
        AppSettings settings)
    {
        _db = db;
        _orders = orders;
        _history = history;
        _holdDecision = holdDecision;
        _holdService = holdService;
        _channelDecision = channelDecision;
        _filesNeededDecision = filesNeededDecision;
        _pixfizzIngest = pixfizzIngest;
        _dakisIngest = dakisIngest;
        _dakisParser = dakisParser;
        _settings = settings;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Verify — two lists, reconcile, both should be empty at the end
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Convenience: build both lists from date range and reconcile.
    /// </summary>
    public void VerifyRecentOrders(int days)
    {
        try
        {
            var cutoff = days > 0 ? DateTime.Now.AddDays(-days) : DateTime.MinValue;

            var folderList = new Dictionary<string, (string Path, string Source)>(StringComparer.OrdinalIgnoreCase);
            ScanFoldersIntoList(_settings.OrderOutputPath, "pixfizz", cutoff, folderList);
            ScanFoldersIntoList(_settings.DakisWatchFolder, "dakis", cutoff, folderList);

            var dbList = LoadDbOrderList(days);

            Verify(folderList, dbList);
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Parsing, "Verify failed", ex: ex);
        }
    }

    /// <summary>
    /// Load DB list for a date range: order ID → (dbId, folderPath, sourceCode).
    /// </summary>
    private Dictionary<string, (int Id, string FolderPath, string SourceCode)> LoadDbOrderList(int days)
    {
        var cutoff = days > 0 ? DateTime.Now.AddDays(-days) : DateTime.MinValue;
        var dbList = new Dictionary<string, (int Id, string FolderPath, string SourceCode)>(StringComparer.OrdinalIgnoreCase);
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT o.id, o.external_order_id, o.folder_path, o.source_code
            FROM orders o
            WHERE o.pickup_store_id = @storeId
              AND (@daysBack = 0 OR o.ordered_at >= @cutoff)
              AND o.is_test = 0
            """;
        cmd.Parameters.AddWithValue("@storeId", _settings.StoreId);
        cmd.Parameters.AddWithValue("@daysBack", days);
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("yyyy-MM-dd"));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var eid = reader.GetString(1);
            dbList[eid] = (
                reader.GetInt32(0),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3));
        }
        return dbList;
    }

    /// <summary>
    /// Reconcile two lists: folders on disk vs orders in SQLite.
    /// Can be called with full date range (startup) or a single order (on click).
    /// Both lists should be empty at the end.
    /// </summary>
    public void Verify(
        Dictionary<string, (string Path, string Source)> folderList,
        Dictionary<string, (int Id, string FolderPath, string SourceCode)> dbList)
    {
        int inserted = 0, repaired = 0, errors = 0;
        int matchCount = 0;

        using var conn = _db.OpenConnection();

        // ── Reconcile: orders in BOTH lists ──
        var matched = folderList.Keys.Intersect(dbList.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var orderId in matched)
        {
            var folder = folderList[orderId];
            var db = dbList[orderId];

            // Verify files on disk using OrderHelpers.VerifyFile (the ONE verification function)
            Core.Models.OrderSource source;
            try { source = Core.Models.OrderSourceExtensions.FromCode(db.SourceCode); }
            catch { folderList.Remove(orderId); dbList.Remove(orderId); matchCount++; continue; }

            bool filesRequired = _filesNeededDecision.AreFilesRequired(source, _settings.StoreId, _settings.StoreId);

            if (filesRequired)
            {
                using var itemCmd = conn.CreateCommand();
                itemCmd.CommandText = "SELECT id, image_filepath, image_filename FROM order_items WHERE order_id = @oid";
                itemCmd.Parameters.AddWithValue("@oid", db.Id);

                var itemIssues = new List<string>();
                using (var itemReader = itemCmd.ExecuteReader())
                {
                    while (itemReader.Read())
                    {
                        var filepath = itemReader.IsDBNull(1) ? "" : itemReader.GetString(1);
                        if (string.IsNullOrWhiteSpace(filepath)) continue;

                        var error = OrderHelpers.VerifyFile(filepath);
                        if (error != null)
                        {
                            var filename = itemReader.IsDBNull(2) ? Path.GetFileName(filepath) : itemReader.GetString(2);
                            itemIssues.Add($"{filename}: {error}");
                        }
                    }
                }

                // For Pixfizz: reread TXT and compare with DB (TXT is source of truth)
                if (source == Core.Models.OrderSource.Pixfizz)
                {
                    var txtPath = Path.Combine(folder.Path, "darkroom_ticket.txt");
                    if (File.Exists(txtPath))
                    {
                        // TODO: full TXT-vs-DB compare-and-repair
                        // When implemented: reread TXT, compare item count/sizes/filenames
                        // If mismatch: overwrite DB from TXT, add "repaired at" note
                    }
                }

                if (itemIssues.Count > 0)
                {
                    var note = $"Verify: {itemIssues.Count} file issue(s) — {string.Join("; ", itemIssues.Take(5))}";
                    _history.AddNote(db.Id, note, "system");
                    repaired += itemIssues.Count;
                }
            }

            // Reconciled — remove from both lists
            folderList.Remove(orderId);
            dbList.Remove(orderId);
            matchCount++;
        }

        // ── Leftover in folder list: on disk but not in DB → insert ──
        foreach (var kvp in folderList)
        {
            try
            {
                if (kvp.Value.Source == "pixfizz")
                    InsertPixfizzFromDisk(kvp.Value.Path);
                else
                    InsertDakisFromDisk(kvp.Key, kvp.Value.Path);
                inserted++;
            }
            catch (Exception ex)
            {
                AppLog.Info($"Verify insert skip {kvp.Key}: {ex.Message}");
                errors++;
            }
        }

        // ── Leftover in DB list: in DB but not on disk → error state ──
        foreach (var kvp in dbList)
        {
            _history.AddNote(kvp.Value.Id, "Verify: order folder not found on disk", "system");
            errors++;
        }

        NeedsRefresh = inserted > 0 || repaired > 0 || errors > 0;
        AppLog.Info($"Verify complete: {matchCount} matched, {inserted} inserted, {repaired} repaired, {errors} errors");
    }

    /// <summary>
    /// Verify a single order — called when operator clicks an order in the tree.
    /// </summary>
    public void VerifyOrder(OrderTreeItem order)
    {
        var folderList = new Dictionary<string, (string Path, string Source)>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(order.FolderPath))
            folderList[order.ExternalOrderId] = (order.FolderPath, order.SourceCode);

        var dbList = new Dictionary<string, (int Id, string FolderPath, string SourceCode)>(StringComparer.OrdinalIgnoreCase)
        {
            [order.ExternalOrderId] = (order.DbId, order.FolderPath, order.SourceCode)
        };

        Verify(folderList, dbList);
    }

    // ── Verify helpers ──

    private static void ScanFoldersIntoList(string root, string source, DateTime cutoff,
        Dictionary<string, (string Path, string Source)> list)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

        foreach (var dir in Directory.GetDirectories(root))
        {
            // Skip non-order folders (Archived, Artwork, etc.)
            bool hasOrderFile = source == "pixfizz"
                ? File.Exists(Path.Combine(dir, "darkroom_ticket.txt"))
                : File.Exists(Path.Combine(dir, "order.yml"));
            if (!hasOrderFile) continue;

            if (cutoff > DateTime.MinValue && new DirectoryInfo(dir).LastWriteTime < cutoff) continue;

            var folderName = Path.GetFileName(dir);
            var orderId = source == "dakis" && folderName.StartsWith("order ", StringComparison.OrdinalIgnoreCase)
                ? folderName[6..].Trim()
                : folderName;

            // For Pixfizz: order ID comes from TXT, but folder name is the short ID
            // Read the TXT to get the real order ID
            if (source == "pixfizz")
            {
                var txtPath = Path.Combine(dir, "darkroom_ticket.txt");
                try
                {
                    foreach (var line in File.ReadLines(txtPath))
                    {
                        if (line.StartsWith("ExtOrderNum=", StringComparison.OrdinalIgnoreCase))
                        {
                            orderId = line[12..].Trim();
                            break;
                        }
                        if (line.StartsWith("Orderid=", StringComparison.OrdinalIgnoreCase))
                        {
                            orderId = line[8..].Trim();
                            break;
                        }
                    }
                }
                catch { /* use folder name as fallback */ }
            }

            list.TryAdd(orderId, (dir, source));
        }
    }

    private void InsertPixfizzFromDisk(string dir)
    {
        var txtPath = Path.Combine(dir, "darkroom_ticket.txt");
        var txtContent = File.ReadAllText(txtPath);

        // Build local file index for path resolution
        var artworkDir = Path.Combine(dir, "artwork");
        var localFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(artworkDir))
            foreach (var f in Directory.GetFiles(artworkDir, "*.jpg"))
                localFiles[Path.GetFileName(f)] = f;

        var parsed = HitePhoto.Shared.Parsers.PixfizzTxtParser.ParseContent(txtContent, ftpPath =>
        {
            var fname = Path.GetFileName(ftpPath);
            return localFiles.TryGetValue(fname, out var local) ? local : ftpPath;
        });
        if (parsed == null) throw new InvalidOperationException("TXT parse returned null");

        var items = TxtItemConverter.ToUnifiedItems(parsed);

        var order = new UnifiedOrder
        {
            ExternalOrderId = parsed.OrderId ?? Path.GetFileName(dir),
            ExternalSource = "pixfizz",
            CustomerFirstName = parsed.FirstName,
            CustomerLastName = parsed.LastName,
            CustomerEmail = parsed.Email,
            OrderedAt = DateTime.TryParse(parsed.ReceivedAt, out var dt) ? dt : DateTime.Now,
            Notes = parsed.Notes,
            FolderPath = dir,
            Location = parsed.Location,
            DownloadStatus = "complete",
            Items = items
        };

        _orders.InsertOrder(order, _settings.StoreId);
    }

    private void InsertDakisFromDisk(string orderId, string dir)
    {
        var ymlContent = File.ReadAllText(Path.Combine(dir, "order.yml"));
        var raw = new RawOrder(orderId, "dakis", ymlContent,
            new Dictionary<string, string> { ["folder_path"] = dir });
        var order = _dakisParser.Parse(raw);
        _orders.InsertOrder(order, _settings.StoreId);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Data loading — reads from local SQLite only
    // ══════════════════════════════════════════════════════════════════════

    public void LoadOrders()
    {
        try
        {
            using var conn = _db.OpenConnection();
            IsDbConnected = true;

            var pending = LoadOrdersWithStatus(conn, "new", "in_progress");
            var printed = LoadOrdersWithStatus(conn, "ready", "notified", "picked_up");
            var otherStore = LoadOtherStoreOrders(conn);

            RebuildTree(PendingOrders, pending, verifyFiles: true);
            RebuildTree(PrintedOrders, printed, verifyFiles: false);
            RebuildTree(OtherStoreOrders, otherStore, verifyFiles: false);

            var total = pending.Count + printed.Count + otherStore.Count;
            StatusText = $"{pending.Count} pending, {printed.Count} printed, {otherStore.Count} other store";
        }
        catch (Exception ex)
        {
            IsDbConnected = false;
            AlertCollector.Error(AlertCategory.Database,
                "Failed to load orders from SQLite", ex: ex);
        }
    }

    private List<OrderRow> LoadOrdersWithStatus(SqliteConnection conn, params string[] statusCodes)
    {
        var results = new List<OrderRow>();
        using var cmd = conn.CreateCommand();

        var placeholders = string.Join(",", statusCodes.Select((_, i) => $"@s{i}"));
        cmd.CommandText = $"""
            SELECT o.id, o.external_order_id, o.source_code, o.status_code,
                   o.customer_first_name, o.customer_last_name,
                   o.customer_email, o.customer_phone,
                   o.ordered_at, o.total_amount, o.is_held, o.is_transfer,
                   o.folder_path, o.special_instructions, o.download_status,
                   s.short_name AS store_name
            FROM orders o
            LEFT JOIN stores s ON s.id = o.pickup_store_id
            WHERE o.pickup_store_id = @storeId
              AND o.status_code IN ({placeholders})
              AND o.is_test = 0
              AND (@daysBack = 0 OR o.ordered_at >= @cutoff)
            ORDER BY o.ordered_at DESC
            """;
        cmd.Parameters.AddWithValue("@storeId", _settings.StoreId);
        cmd.Parameters.AddWithValue("@daysBack", _settings.DaysToLoad);
        cmd.Parameters.AddWithValue("@cutoff", DateTime.Now.AddDays(-Math.Max(_settings.DaysToLoad, 1)).ToString("yyyy-MM-dd"));
        for (int i = 0; i < statusCodes.Length; i++)
            cmd.Parameters.AddWithValue($"@s{i}", statusCodes[i]);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadOrderRow(reader));

        return results;
    }

    private List<OrderRow> LoadOtherStoreOrders(SqliteConnection conn)
    {
        var results = new List<OrderRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT o.id, o.external_order_id, o.source_code, o.status_code,
                   o.customer_first_name, o.customer_last_name,
                   o.customer_email, o.customer_phone,
                   o.ordered_at, o.total_amount, o.is_held, o.is_transfer,
                   o.folder_path, o.special_instructions, o.download_status,
                   s.short_name AS store_name
            FROM orders o
            LEFT JOIN stores s ON s.id = o.pickup_store_id
            WHERE o.pickup_store_id != @storeId
              AND o.status_code NOT IN ('picked_up', 'cancelled')
              AND o.is_test = 0
            ORDER BY o.ordered_at DESC
            """;
        cmd.Parameters.AddWithValue("@storeId", _settings.StoreId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadOrderRow(reader));

        return results;
    }

    private static OrderRow ReadOrderRow(SqliteDataReader reader) => new(
        Id: reader.GetInt32(0),
        ExternalOrderId: reader.GetString(1),
        SourceCode: reader.IsDBNull(2) ? "" : reader.GetString(2),
        StatusCode: reader.IsDBNull(3) ? "" : reader.GetString(3),
        CustomerFirstName: reader.IsDBNull(4) ? "" : reader.GetString(4),
        CustomerLastName: reader.IsDBNull(5) ? "" : reader.GetString(5),
        CustomerEmail: reader.IsDBNull(6) ? "" : reader.GetString(6),
        CustomerPhone: reader.IsDBNull(7) ? "" : reader.GetString(7),
        OrderedAt: reader.IsDBNull(8) ? null : reader.GetString(8),
        TotalAmount: reader.IsDBNull(9) ? 0 : reader.GetDecimal(9),
        IsHeld: reader.GetInt32(10) == 1,
        IsTransfer: reader.GetInt32(11) == 1,
        FolderPath: reader.IsDBNull(12) ? "" : reader.GetString(12),
        SpecialInstructions: reader.IsDBNull(13) ? "" : reader.GetString(13),
        DownloadStatus: reader.IsDBNull(14) ? "" : reader.GetString(14),
        StoreName: reader.IsDBNull(15) ? "" : reader.GetString(15));

    // ══════════════════════════════════════════════════════════════════════
    //  Tree building
    // ══════════════════════════════════════════════════════════════════════

    private void RebuildTree(ObservableCollection<OrderTreeItem> target, List<OrderRow> orders, bool verifyFiles)
    {
        var filtered = ApplyFilters(orders);
        var sorted = ApplySort(filtered);

        if (sorted.Count == 0) { target.Clear(); return; }

        // Batch-load all items for all orders in one query
        var orderIds = sorted.Select(o => o.Id).ToList();
        var allItems = BatchLoadItems(orderIds);

        // Build all tree items FIRST, then swap into collection in one batch
        var built = new List<OrderTreeItem>(sorted.Count);

        foreach (var order in sorted)
        {
            var treeItem = new OrderTreeItem
            {
                DbId = order.Id,
                ExternalOrderId = order.ExternalOrderId,
                CustomerName = $"{order.CustomerFirstName} {order.CustomerLastName}".Trim(),
                SourceCode = order.SourceCode,
                StatusCode = order.StatusCode,
                StoreName = order.StoreName,
                OrderedAt = order.OrderedAt != null && DateTime.TryParse(order.OrderedAt, out var dt) ? dt : null,
                IsHeld = order.IsHeld,
                IsTransfer = order.IsTransfer,
                FolderPath = order.FolderPath,
                IsExpanded = true
            };

            var items = allItems.TryGetValue(order.Id, out var list) ? list : new();
            BuildSizeGroups(treeItem, items, verifyFiles: false);

            built.Add(treeItem);
        }

        // Single batch update — minimizes WPF re-render events
        target.Clear();
        foreach (var item in built)
            target.Add(item);
    }

    private Dictionary<int, List<ItemRow>> BatchLoadItems(List<int> orderIds)
    {
        var result = new Dictionary<int, List<ItemRow>>();
        if (orderIds.Count == 0) return result;

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();

        var placeholders = string.Join(",", orderIds.Select((_, i) => $"@id{i}"));
        cmd.CommandText = $"""
            SELECT oi.order_id, oi.id, oi.size_label, oi.media_type, oi.quantity,
                   oi.image_filename, oi.image_filepath, oi.channel_number,
                   oi.is_noritsu, oi.is_printed, oi.options_json
            FROM order_items oi
            WHERE oi.order_id IN ({placeholders})
            ORDER BY oi.order_id, oi.size_label, oi.media_type
            """;
        for (int i = 0; i < orderIds.Count; i++)
            cmd.Parameters.AddWithValue($"@id{i}", orderIds[i]);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var orderId = reader.GetInt32(0);
            var item = new ItemRow(
                Id: reader.GetInt32(1),
                SizeLabel: reader.IsDBNull(2) ? "" : reader.GetString(2),
                MediaType: reader.IsDBNull(3) ? "" : reader.GetString(3),
                Quantity: reader.GetInt32(4),
                ImageFilename: reader.IsDBNull(5) ? "" : reader.GetString(5),
                ImageFilepath: reader.IsDBNull(6) ? "" : reader.GetString(6),
                ChannelNumber: reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                IsNoritsu: reader.GetInt32(8) == 1,
                IsPrinted: reader.GetInt32(9) == 1,
                OptionsJson: reader.IsDBNull(10) ? "[]" : reader.GetString(10));

            if (!result.ContainsKey(orderId))
                result[orderId] = new List<ItemRow>();
            result[orderId].Add(item);
        }
        return result;
    }

    private static List<ItemRow> LoadItems(SqliteConnection conn, int orderId)
    {
        var items = new List<ItemRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, size_label, media_type, quantity, image_filename,
                   image_filepath, channel_number, is_noritsu, is_printed, options_json
            FROM order_items WHERE order_id = @id
            ORDER BY size_label, media_type
            """;
        cmd.Parameters.AddWithValue("@id", orderId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new ItemRow(
                Id: reader.GetInt32(0),
                SizeLabel: reader.IsDBNull(1) ? "" : reader.GetString(1),
                MediaType: reader.IsDBNull(2) ? "" : reader.GetString(2),
                Quantity: reader.GetInt32(3),
                ImageFilename: reader.IsDBNull(4) ? "" : reader.GetString(4),
                ImageFilepath: reader.IsDBNull(5) ? "" : reader.GetString(5),
                ChannelNumber: reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                IsNoritsu: reader.GetInt32(7) == 1,
                IsPrinted: reader.GetInt32(8) == 1,
                OptionsJson: reader.IsDBNull(9) ? "[]" : reader.GetString(9)));
        }
        return items;
    }

    private static void BuildSizeGroups(OrderTreeItem treeItem, List<ItemRow> items, bool verifyFiles)
    {
        int totalImages = 0;
        bool hasMissing = false;

        var groups = items
            .GroupBy(i => new { Size = string.IsNullOrEmpty(i.SizeLabel) ? "(no size)" : i.SizeLabel, i.MediaType })
            .ToList();

        foreach (var group in groups)
        {
            int missingCount = 0;
            int printedCount = 0;

            foreach (var item in group)
            {
                if (verifyFiles && !string.IsNullOrEmpty(item.ImageFilepath))
                {
                    var error = OrderHelpers.VerifyFile(item.ImageFilepath);
                    if (error != null) missingCount++;
                }
                if (item.IsPrinted) printedCount++;
            }

            var sizeItem = new SizeTreeItem
            {
                SizeLabel = group.Key.Size,
                MediaType = group.Key.MediaType,
                ImageCount = group.Sum(i => i.Quantity),
                PrintedCount = printedCount,
                MissingFileCount = missingCount,
                ChannelNumber = group.First().ChannelNumber
            };

            treeItem.Sizes.Add(sizeItem);
            totalImages += sizeItem.ImageCount;
            if (missingCount > 0) hasMissing = true;
        }

        treeItem.TotalImages = totalImages;
        treeItem.HasMissingFiles = hasMissing;
        treeItem.HasUnmapped = treeItem.Sizes.Any(s => s.IsUnmapped);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Filtering & sorting
    // ══════════════════════════════════════════════════════════════════════

    private List<OrderRow> ApplyFilters(List<OrderRow> orders)
    {
        var result = orders.AsEnumerable();

        if (_sourceFilter != "All")
            result = result.Where(o => o.SourceCode.Equals(_sourceFilter, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var search = _searchText.Trim();
            result = result.Where(o =>
                o.ExternalOrderId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                o.CustomerFirstName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                o.CustomerLastName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                o.CustomerEmail.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                o.CustomerPhone.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        return result.ToList();
    }

    private List<OrderRow> ApplySort(List<OrderRow> orders)
    {
        return _sortMode switch
        {
            "Customer Name" => orders.OrderBy(o => $"{o.CustomerFirstName} {o.CustomerLastName}".Trim()).ToList(),
            "Order ID" => orders.OrderBy(o => o.ExternalOrderId).ToList(),
            _ => orders.OrderByDescending(o => o.OrderedAt ?? "").ToList()
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Actions
    // ══════════════════════════════════════════════════════════════════════

    public void ToggleHold(string operatorName)
    {
        if (_selectedOrder == null) return;
        var newState = _holdService.ToggleHold(_selectedOrder.DbId, operatorName);
        _selectedOrder.IsHeld = newState;
    }

    public void OnOrderSelected()
    {
        OrderNotes.Clear();
        if (_selectedOrder == null) return;

        var notes = _history.GetNotes(_selectedOrder.DbId);
        foreach (var note in notes)
            OrderNotes.Add(note);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Ingest
    // ══════════════════════════════════════════════════════════════════════

    public async Task RunPixfizzPollAsync(CancellationToken ct)
    {
        await _pixfizzIngest.PollAsync(ct);
        LoadOrders(); // Refresh tree after new orders ingested
    }

    public void RunDakisScan()
    {
        _dakisIngest.ScanFolder();
        LoadOrders();
    }
}

// ── Internal data records (not exposed outside ViewModel) ──

internal record OrderRow(
    int Id, string ExternalOrderId, string SourceCode, string StatusCode,
    string CustomerFirstName, string CustomerLastName,
    string CustomerEmail, string CustomerPhone,
    string? OrderedAt, decimal TotalAmount,
    bool IsHeld, bool IsTransfer,
    string FolderPath, string SpecialInstructions, string DownloadStatus,
    string StoreName);

internal record ItemRow(
    int Id, string SizeLabel, string MediaType, int Quantity,
    string ImageFilename, string ImageFilepath,
    int ChannelNumber, bool IsNoritsu, bool IsPrinted, string OptionsJson);
