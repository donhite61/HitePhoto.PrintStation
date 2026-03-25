using System.Collections.ObjectModel;
using System.IO;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Core.Decisions;
using HitePhoto.PrintStation.Core.Ingest;
using HitePhoto.PrintStation.Core.Services;
using HitePhoto.PrintStation.Data;
using HitePhoto.PrintStation.Data.Repositories;
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
    private readonly IOrderVerifier _verifier;
    private readonly IPrintService _printService;
    private readonly PixfizzIngestService _pixfizzIngest;
    private readonly DakisIngestService _dakisIngest;
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
        IOrderVerifier verifier,
        IPrintService printService,
        PixfizzIngestService pixfizzIngest,
        DakisIngestService dakisIngest,
        AppSettings settings)
    {
        _db = db;
        _orders = orders;
        _history = history;
        _holdDecision = holdDecision;
        _holdService = holdService;
        _channelDecision = channelDecision;
        _filesNeededDecision = filesNeededDecision;
        _verifier = verifier;
        _printService = printService;
        _pixfizzIngest = pixfizzIngest;
        _dakisIngest = dakisIngest;
        _settings = settings;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Verify — delegates to IOrderVerifier (single source of truth)
    // ══════════════════════════════════════════════════════════════════════

    public void VerifyRecentOrders(int days)
    {
        try
        {
            var result = _verifier.VerifyRecentOrders(days);
            NeedsRefresh = result.Inserted > 0 || result.Repaired > 0 || result.Errors > 0;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Parsing, "Verify failed", ex: ex);
        }
    }

    /// <summary>
    /// Verify a single order — called when operator clicks an order in the tree.
    /// </summary>
    public void VerifyOrder(OrderTreeItem order)
    {
        var result = _verifier.VerifyOrder(
            order.ExternalOrderId, order.FolderPath, order.SourceCode, order.DbId);
        NeedsRefresh = result.Repaired > 0 || result.Errors > 0;
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
            ORDER BY o.ordered_at DESC
            """;
        cmd.Parameters.AddWithValue("@storeId", _settings.StoreId);
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

    // ══════════════════════════════════════════════════════════════════════
    //  Printing
    // ══════════════════════════════════════════════════════════════════════

    public SendResult PrintOrder(int orderId, string externalOrderId, string folderPath, string sourceCode)
    {
        // Verify before printing — same check every order gets
        _verifier.VerifyOrder(externalOrderId, folderPath, sourceCode, orderId);

        var result = _printService.SendToPrinter(orderId);
        NeedsRefresh = true;
        return result;
    }

    public List<Core.Models.ChannelInfo> GetAllChannels() => _orders.GetAllChannels();

    // ══════════════════════════════════════════════════════════════════════
    //  Ingest
    // ══════════════════════════════════════════════════════════════════════

    public async Task RunPixfizzPollAsync(CancellationToken ct)
    {
        await _pixfizzIngest.PollAsync(ct);
        LoadOrders();
    }

    public void RunDakisScan()
    {
        _dakisIngest.ScanFolder();
        LoadOrders();
    }

    public void StartDakisWatcher() => _dakisIngest.StartWatching();
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
