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
    private readonly ITransferService _transferService;
    private readonly PixfizzIngestService _pixfizzIngest;
    private readonly DakisIngestService _dakisIngest;
    private readonly AppSettings _settings;

    // ── Observable collections for tree views ──
    public ObservableCollection<OrderTreeItem> PendingOrders { get; } = new();
    public ObservableCollection<OrderTreeItem> PrintedOrders { get; } = new();
    public ObservableCollection<OrderTreeItem> OtherStoreOrders { get; } = new();

    private Dictionary<int, string> _channelNames = new();
    private bool _csvChannelNamesLoaded;
    private Dictionary<string, int> _channelByRoutingKey = new();
    private Dictionary<string, string> _layoutByRoutingKey = new();
    private bool _channelNamesDirty = true;

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
        ITransferService transferService,
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
        _transferService = transferService;
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

            if (_channelNamesDirty)
            {
                var allChannels = _orders.GetAllChannels();
                _channelByRoutingKey = allChannels
                    .ToDictionary(c => c.RoutingKey, c => c.ChannelNumber);
                _layoutByRoutingKey = allChannels
                    .Where(c => !string.IsNullOrEmpty(c.Description))
                    .ToDictionary(c => c.RoutingKey, c => c.Description);

                // Channel names come from Noritsu CSV — read once, not on every refresh
                if (!_csvChannelNamesLoaded)
                {
                    _channelNames = new Dictionary<int, string>();
                    if (!string.IsNullOrWhiteSpace(_settings.ChannelsCsvPath))
                    {
                        var csvChannels = new Core.Processing.ChannelsCsvReader(_settings.ChannelsCsvPath).Load();
                        foreach (var c in csvChannels)
                        {
                            _channelNames.TryAdd(c.ChannelNumber,
                                $"{c.SizeLabel} {c.MediaType}".Trim());
                        }
                    }
                    _csvChannelNamesLoaded = true;
                }
                _channelNamesDirty = false;
            }

            var pending = LoadPendingOrders(conn);
            var printed = LoadPrintedOrders(conn);
            var otherStore = LoadOtherStoreOrders(conn);

            DiffAndPatch(PendingOrders, pending, verifyFiles: true);
            DiffAndPatch(PrintedOrders, printed, verifyFiles: false);
            DiffAndPatch(OtherStoreOrders, otherStore, verifyFiles: false);

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

    private List<OrderRow> LoadPendingOrders(SqliteConnection conn)
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
            WHERE o.files_local = 1
              AND o.is_test = 0
              AND o.status_code NOT IN ('cancelled')
              AND EXISTS (
                  SELECT 1 FROM order_items oi
                  WHERE oi.order_id = o.id AND oi.is_printed = 0
              )
            ORDER BY o.ordered_at DESC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadOrderRow(reader));
        return results;
    }

    private List<OrderRow> LoadPrintedOrders(SqliteConnection conn)
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
            WHERE o.files_local = 1
              AND o.is_test = 0
              AND o.status_code NOT IN ('cancelled')
              AND EXISTS (
                  SELECT 1 FROM order_items oi WHERE oi.order_id = o.id
              )
              AND NOT EXISTS (
                  SELECT 1 FROM order_items oi
                  WHERE oi.order_id = o.id AND oi.is_printed = 0
              )
            ORDER BY o.ordered_at DESC
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadOrderRow(reader));
        return results;
    }

    private List<OrderRow> LoadOtherStoreOrders(SqliteConnection conn)
    {
        var results = new List<OrderRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT o.id, o.external_order_id, o.source_code, o.status_code,
                   o.customer_first_name, o.customer_last_name,
                   o.customer_email, o.customer_phone,
                   o.ordered_at, o.total_amount, o.is_held, o.is_transfer,
                   o.folder_path, o.special_instructions, o.download_status,
                   s.short_name AS store_name
            FROM orders o
            LEFT JOIN stores s ON s.id = o.pickup_store_id
            WHERE o.files_local = 0
              AND o.status_code NOT IN ('{OrderStatusCode.PickedUp}', '{OrderStatusCode.Cancelled}')
              AND o.is_test = 0
            ORDER BY o.ordered_at DESC
            """;

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

    /// <summary>
    /// In-place diff-and-patch: updates existing tree items instead of clear-and-rebuild.
    /// Preserves WPF selection, expansion, and scroll position.
    /// </summary>
    private void DiffAndPatch(ObservableCollection<OrderTreeItem> target, List<OrderRow> orders, bool verifyFiles)
    {
        var filtered = ApplyFilters(orders);
        var sorted = ApplySort(filtered);

        if (sorted.Count == 0) { target.Clear(); return; }

        var orderIds = sorted.Select(o => o.Id).ToList();
        var allItems = BatchLoadItems(orderIds);

        // Lookup existing items by DbId for O(1) matching
        var existingByDbId = new Dictionary<int, OrderTreeItem>();
        foreach (var item in target)
            existingByDbId[item.DbId] = item;

        for (int i = 0; i < sorted.Count; i++)
        {
            var row = sorted[i];
            var items = allItems.TryGetValue(row.Id, out var list) ? list : new();

            if (i < target.Count && target[i].DbId == row.Id)
            {
                // Same item at same position — update in place
                UpdateOrderProperties(target[i], row);
                DiffSizes(target[i], items, verifyFiles);
            }
            else if (existingByDbId.TryGetValue(row.Id, out var existing))
            {
                // Item exists but at wrong position — move it
                int oldIndex = target.IndexOf(existing);
                if (oldIndex != i)
                    target.Move(oldIndex, i);
                UpdateOrderProperties(existing, row);
                DiffSizes(existing, items, verifyFiles);
            }
            else
            {
                // New item — create and insert
                var treeItem = CreateOrderTreeItem(row);
                BuildSizeGroups(treeItem, items, verifyFiles);
                target.Insert(i, treeItem);
            }
        }

        // Remove items that are no longer in the filtered/sorted set
        while (target.Count > sorted.Count)
            target.RemoveAt(target.Count - 1);
    }

    private static OrderTreeItem CreateOrderTreeItem(OrderRow order) => new()
    {
        DbId = order.Id,
        ExternalOrderId = order.ExternalOrderId,
        CustomerName = $"{order.CustomerFirstName} {order.CustomerLastName}".Trim(),
        CustomerPhone = order.CustomerPhone,
        CustomerEmail = order.CustomerEmail,
        SourceCode = order.SourceCode,
        StatusCode = order.StatusCode,
        StoreName = order.StoreName,
        OrderedAt = order.OrderedAt != null && DateTime.TryParse(order.OrderedAt, out var dt) ? dt : null,
        IsHeld = order.IsHeld,
        IsTransfer = order.IsTransfer,
        FolderPath = order.FolderPath,
        IsExpanded = true
    };

    private static void UpdateOrderProperties(OrderTreeItem existing, OrderRow row)
    {
        var name = $"{row.CustomerFirstName} {row.CustomerLastName}".Trim();
        if (existing.ExternalOrderId != row.ExternalOrderId) existing.ExternalOrderId = row.ExternalOrderId;
        if (existing.CustomerName != name) existing.CustomerName = name;
        if (existing.CustomerPhone != row.CustomerPhone) existing.CustomerPhone = row.CustomerPhone;
        if (existing.CustomerEmail != row.CustomerEmail) existing.CustomerEmail = row.CustomerEmail;
        if (existing.SourceCode != row.SourceCode) existing.SourceCode = row.SourceCode;
        if (existing.StatusCode != row.StatusCode) existing.StatusCode = row.StatusCode;
        if (existing.StoreName != row.StoreName) existing.StoreName = row.StoreName;
        if (existing.IsHeld != row.IsHeld) existing.IsHeld = row.IsHeld;
        if (existing.IsTransfer != row.IsTransfer) existing.IsTransfer = row.IsTransfer;
        if (existing.FolderPath != row.FolderPath) existing.FolderPath = row.FolderPath;

        var orderedAt = row.OrderedAt != null && DateTime.TryParse(row.OrderedAt, out var dt) ? dt : (DateTime?)null;
        if (existing.OrderedAt != orderedAt) existing.OrderedAt = orderedAt;
    }

    private record SizeGroupResult(
        string SizeLabel, string MediaType,
        int ImageCount, int PrintedCount, int MissingCount,
        int ChannelNumber, string ChannelName, string? LayoutName,
        List<HitePhoto.Shared.Models.OrderItem> Items);

    private SizeGroupResult ProcessSizeGroup(IEnumerable<ItemRow> group, string sizeLabel, string optionsKey, bool verifyFiles)
    {
        int missingCount = 0;
        int printedCount = 0;
        var orderItems = new List<HitePhoto.Shared.Models.OrderItem>();
        int totalQty = 0;

        // Routing key is size + options (e.g. "5x7|white border")
        var routingKey = string.IsNullOrEmpty(optionsKey) ? sizeLabel.Trim().ToLowerInvariant() : $"{sizeLabel.Trim().ToLowerInvariant()}|{optionsKey}";
        _channelByRoutingKey.TryGetValue(routingKey, out int channelNumber);

        foreach (var item in group)
        {
            if (verifyFiles && item.IsLocalProduction && !string.IsNullOrEmpty(item.ImageFilepath))
            {
                var error = OrderHelpers.VerifyFile(item.ImageFilepath);
                if (error != null) missingCount++;
            }
            if (item.IsPrinted) printedCount += item.Quantity;
            totalQty += item.Quantity;

            orderItems.Add(new HitePhoto.Shared.Models.OrderItem
            {
                Id = item.Id,
                SizeLabel = item.SizeLabel,
                MediaType = item.MediaType,
                Quantity = item.Quantity,
                ImageFilename = item.ImageFilename,
                ImageFilepath = item.ImageFilepath,
                ChannelNumber = channelNumber,
                IsPrinted = item.IsPrinted,
                OptionsJson = item.OptionsJson
            });
        }

        var chName = channelNumber > 0 && _channelNames.TryGetValue(channelNumber, out var n) ? n : "";
        _layoutByRoutingKey.TryGetValue(routingKey, out var layoutName);
        return new SizeGroupResult(sizeLabel, optionsKey, totalQty, printedCount, missingCount, channelNumber, chName, layoutName, orderItems);
    }

    private void DiffSizes(OrderTreeItem treeItem, List<ItemRow> items, bool verifyFiles)
    {
        var groups = items
            .GroupBy(i => new {
                Size = string.IsNullOrEmpty(i.SizeLabel) ? "(no size)" : i.SizeLabel,
                OptionsKey = OrderHelpers.BuildOptionsKey(i.OptionsJson)
            })
            .ToList();

        var existingByKey = new Dictionary<string, SizeTreeItem>();
        foreach (var sz in treeItem.Sizes)
            existingByKey[$"{sz.SizeLabel}|{sz.MediaType}"] = sz;

        int totalImages = 0;
        bool hasMissing = false;

        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var r = ProcessSizeGroup(group, group.Key.Size, group.Key.OptionsKey, verifyFiles);
            var key = $"{r.SizeLabel}|{r.MediaType}";

            totalImages += r.ImageCount;
            if (r.MissingCount > 0) hasMissing = true;

            if (existingByKey.TryGetValue(key, out var existing))
            {
                if (existing.ImageCount != r.ImageCount) existing.ImageCount = r.ImageCount;
                if (existing.PrintedCount != r.PrintedCount) existing.PrintedCount = r.PrintedCount;
                if (existing.MissingFileCount != r.MissingCount) existing.MissingFileCount = r.MissingCount;
                if (existing.ChannelNumber != r.ChannelNumber) existing.ChannelNumber = r.ChannelNumber;
                if (existing.ChannelName != r.ChannelName) existing.ChannelName = r.ChannelName;
                if (existing.LayoutName != r.LayoutName) existing.LayoutName = r.LayoutName;
                existing.Items = r.Items;

                int currentIndex = treeItem.Sizes.IndexOf(existing);
                if (currentIndex != i)
                    treeItem.Sizes.Move(currentIndex, i);
            }
            else
            {
                var sizeItem = new SizeTreeItem
                {
                    SizeLabel = r.SizeLabel, MediaType = r.MediaType,
                    ImageCount = r.ImageCount, PrintedCount = r.PrintedCount,
                    MissingFileCount = r.MissingCount, ChannelNumber = r.ChannelNumber,
                    ChannelName = r.ChannelName, LayoutName = r.LayoutName,
                    Items = r.Items, ParentOrder = treeItem
                };
                treeItem.Sizes.Insert(i, sizeItem);
            }
        }

        for (int i = treeItem.Sizes.Count - 1; i >= groups.Count; i--)
            treeItem.Sizes.RemoveAt(i);

        treeItem.TotalImages = totalImages;
        treeItem.HasMissingFiles = hasMissing;
        treeItem.HasUnmapped = treeItem.Sizes.Any(s => s.IsUnmapped);
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
                   oi.image_filename, oi.image_filepath,
                   oi.is_noritsu, oi.is_local_production, oi.is_printed, oi.options_json
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
                IsNoritsu: reader.GetInt32(7) == 1,
                IsLocalProduction: reader.GetInt32(8) == 1,
                IsPrinted: reader.GetInt32(9) == 1,
                OptionsJson: reader.IsDBNull(10) ? "[]" : reader.GetString(10));

            if (!result.ContainsKey(orderId))
                result[orderId] = new List<ItemRow>();
            result[orderId].Add(item);
        }
        return result;
    }



    private void BuildSizeGroups(OrderTreeItem treeItem, List<ItemRow> items, bool verifyFiles)
    {
        int totalImages = 0;
        bool hasMissing = false;

        var groups = items
            .GroupBy(i => new {
                Size = string.IsNullOrEmpty(i.SizeLabel) ? "(no size)" : i.SizeLabel,
                OptionsKey = OrderHelpers.BuildOptionsKey(i.OptionsJson)
            });

        foreach (var group in groups)
        {
            var r = ProcessSizeGroup(group, group.Key.Size, group.Key.OptionsKey, verifyFiles);
            var sizeItem = new SizeTreeItem
            {
                SizeLabel = r.SizeLabel, MediaType = r.MediaType,
                ImageCount = r.ImageCount, PrintedCount = r.PrintedCount,
                MissingFileCount = r.MissingCount, ChannelNumber = r.ChannelNumber,
                ChannelName = r.ChannelName, LayoutName = r.LayoutName,
                Items = r.Items, ParentOrder = treeItem
            };
            treeItem.Sizes.Add(sizeItem);
            totalImages += r.ImageCount;
            if (r.MissingCount > 0) hasMissing = true;
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
            var itemMatchIds = _orders.FindOrderIdsBySizeLabel(search);
            result = result.Where(o =>
                o.ExternalOrderId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                o.CustomerFirstName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                o.CustomerLastName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                o.CustomerEmail.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                o.CustomerPhone.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                itemMatchIds.Contains(o.Id));
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

    public SendResult PrintOrder(int orderId, string externalOrderId, string folderPath, string sourceCode,
        HashSet<string>? sizeFilter = null)
    {
        _verifier.VerifyOrder(externalOrderId, folderPath, sourceCode, orderId);

        var result = _printService.SendToPrinter(orderId, sizeFilter);
        NeedsRefresh = true;
        return result;
    }

    public List<TransferMismatch> CheckTransferMismatches(int orderId)
        => _transferService.CheckTransferMismatches(orderId);

    public List<Core.Models.ChannelInfo> GetAllChannels() => _orders.GetAllChannels();

    public string GetLocalStoreName() => _orders.GetStoreName(_settings.StoreId);

    public void AssignChannel(string sizeLabel, string mediaType, int channelNumber, string? layoutName = null)
    {
        var routingKey = OrderHelpers.BuildRoutingKey(sizeLabel, mediaType);
        _orders.SaveChannelMapping(routingKey, channelNumber, layoutName);
        _channelNamesDirty = true;
    }

    public void MarkDone(int orderId)
    {
        var allIds = _orders.GetItems(orderId).Select(i => i.Id).ToList();
        _orders.SetItemsPrinted(allIds);
        _history.AddNote(orderId, "Marked done by operator", "operator");
    }

    public void MarkUnprinted(int orderId)
    {
        _orders.SetItemsUnprinted(orderId);
        _history.AddNote(orderId, "Marked unprinted by operator", "operator");
    }

    public void UnassignChannel(string sizeLabel, string mediaType)
    {
        var routingKey = OrderHelpers.BuildRoutingKey(sizeLabel, mediaType);
        _orders.DeleteChannelMapping(routingKey);
        _channelNamesDirty = true;
    }

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
    bool IsNoritsu, bool IsLocalProduction, bool IsPrinted, string OptionsJson);
