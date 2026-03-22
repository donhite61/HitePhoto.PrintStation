using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Core.Models;
using HitePhoto.PrintStation.Core.Processing;
using HitePhoto.PrintStation.Data;
using HitePhoto.PrintStation.UI.ViewModels;
using HitePhoto.Shared.Models;

namespace HitePhoto.PrintStation.UI;

public partial class MainWindow : Window
{
    // ── State ──
    private AppSettings _settings = new();
    private readonly SettingsManager _settingsManager = new();
    private PrintStationDb? _db;

    // ── Order data ──
    private List<Order> _pendingOrders = new();
    private List<Order> _printedOrders = new();
    private List<Order> _otherStoreOrders = new();

    // Items keyed by order ID — pending orders get batch-loaded, printed/other load on click
    private Dictionary<int, List<OrderItem>> _pendingItems = new();
    private Dictionary<int, List<OrderItem>> _onDemandItems = new();

    // ── Tree data ──
    private readonly ObservableCollection<OrderTreeItem> _pendingTreeItems = new();
    private readonly ObservableCollection<OrderTreeItem> _printedTreeItems = new();
    private readonly ObservableCollection<OrderTreeItem> _otherStoreTreeItems = new();

    // ── Filter/sort ──
    private string _searchText = "";
    private string _sourceFilter = "All";
    private string _sortMode = "Date Received";

    // ── Timers ──
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _alertDrainTimer;
    private readonly DispatcherTimer _signalDebounce;

    // ── New-order signal watcher ──
    private FileSystemWatcher? _signalWatcher;
    private static readonly string SignalDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HitePhoto");
    private const string SignalFileName = "new-order.signal";

    // ── Channels ──
    private List<ChannelInfo> _channels = new();

    // ── Currently selected ──
    private OrderTreeItem? _selectedOrderItem;
    private SizeTreeItem? _selectedSizeItem;

    public MainWindow()
    {
        InitializeComponent();

        PendingTree.ItemsSource = _pendingTreeItems;
        PrintedTree.ItemsSource = _printedTreeItems;
        OtherStoreTree.ItemsSource = _otherStoreTreeItems;

        // Refresh timer — queries DB periodically
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refreshTimer.Tick += async (_, _) => await RefreshDataAsync();

        // Search debounce — 200ms after last keystroke
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RebuildVisibleTrees();
        };

        // Alert drain — check for new alerts every 2 seconds
        _alertDrainTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _alertDrainTimer.Tick += (_, _) => DrainAlerts();
        _alertDrainTimer.Start();

        // Signal debounce — IngestService writes signal file after DB upsert,
        // debounce 500ms so rapid batch writes collapse into a single refresh
        _signalDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _signalDebounce.Tick += async (_, _) =>
        {
            _signalDebounce.Stop();
            await RefreshDataAsync();
        };

        Loaded += MainWindow_Loaded;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Startup
    // ══════════════════════════════════════════════════════════════════════

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = _settingsManager.Load();
        ApplySettings();
        LoadChannels();

        // Initialize DB connection
        _db = new PrintStationDb(_settings.ConnectionString);
        var dbError = await _db.TestConnectionAsync();

        if (dbError != null)
        {
            SetDbStatus(false, $"DB offline: {dbError}");
            AlertCollector.Error(AlertCategory.Database,
                "Cannot connect to MariaDB on startup",
                detail: $"Attempted: connect to {_settings.DbHost}:{_settings.DbPort}/{_settings.DbName}. " +
                        $"Expected: successful connection. Found: {dbError}. " +
                        $"State: fresh startup, no data loaded.");
        }
        else
        {
            SetDbStatus(true, "Connected");
            await RefreshDataAsync();
            _refreshTimer.Interval = TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds);
            _refreshTimer.Start();
        }

        // Watch for IngestService new-order signal file
        StartSignalWatcher();

        // Store selection is in Settings — no toolbar filter needed

        // Check for auto-updates
        _ = AutoUpdater.CheckAndPromptAsync(_settings);
    }

    private void ApplySettings()
    {
        AppLog.Enabled = _settings.EnableLogging;

        // Theme — pack URI required because themes are at UI/Themes/ in the assembly resources
        var themeName = _settings.Theme.Equals("Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        var themeUri = new Uri($"pack://application:,,,/UI/Themes/{themeName}.xaml", UriKind.Absolute);

        Application.Current.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = themeUri });

        // Developer mode indicator
        DevModeText.Visibility = _settings.DeveloperMode ? Visibility.Visible : Visibility.Collapsed;
        if (_settings.DeveloperMode)
            Title = "HitePhoto Print Station [DEV MODE]";
        else
            Title = "HitePhoto Print Station";
    }

    private void LoadChannels()
    {
        if (!string.IsNullOrEmpty(_settings.ChannelsCsvPath))
        {
            var reader = new ChannelsCsvReader(_settings.ChannelsCsvPath);
            _channels = reader.Load();
        }
        else
        {
            _channels = new List<ChannelInfo>();
        }
    }

    /// <summary>
    /// Watch %LOCALAPPDATA%\HitePhoto\new-order.signal — written by IngestService
    /// after each successful DB upsert. Triggers immediate DB refresh (debounced 500ms).
    /// The 30-second poll timer stays as a safety net.
    /// </summary>
    private void StartSignalWatcher()
    {
        try
        {
            // Ensure the directory exists so the watcher can attach
            Directory.CreateDirectory(SignalDir);

            _signalWatcher = new FileSystemWatcher(SignalDir, SignalFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            _signalWatcher.Changed += OnSignalFileChanged;
            _signalWatcher.Created += OnSignalFileChanged;

            AppLog.Info($"Signal watcher started on {Path.Combine(SignalDir, SignalFileName)}");
        }
        catch (Exception ex)
        {
            AlertCollector.Warn(AlertCategory.Settings,
                "Could not start new-order signal watcher",
                detail: $"Attempted: create FSW on '{SignalDir}' for '{SignalFileName}'. " +
                        $"Expected: watcher active. Found: exception. " +
                        $"State: poll timer will still detect new orders (every {_settings.RefreshIntervalSeconds}s).",
                ex: ex);
        }
    }

    private void OnSignalFileChanged(object sender, FileSystemEventArgs e)
    {
        // FSW fires on a thread pool thread — marshal to UI and debounce
        Dispatcher.BeginInvoke(() =>
        {
            _signalDebounce.Stop();
            _signalDebounce.Start();
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Data loading
    // ══════════════════════════════════════════════════════════════════════

    private async Task RefreshDataAsync()
    {
        if (_db == null) return;

        try
        {
            // Load all three tabs in parallel
            var pendingTask = _db.GetPendingOrdersAsync(_settings.StoreId);
            var printedTask = _db.GetPrintedOrdersAsync(_settings.StoreId);
            var otherTask = _db.GetOtherStoreOrdersAsync(_settings.StoreId);

            await Task.WhenAll(pendingTask, printedTask, otherTask);

            _pendingOrders = pendingTask.Result;
            _printedOrders = printedTask.Result;
            _otherStoreOrders = otherTask.Result;

            // Batch-load items for pending orders (file verification at load time)
            var pendingIds = _pendingOrders.Select(o => o.Id).ToList();
            _pendingItems = await _db.GetOrderItemsBatchAsync(pendingIds);

            // Verify files exist for pending order items
            VerifyPendingFiles();

            // Rebuild the tree views
            RebuildVisibleTrees();

            UpdateOrderCounts();
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to refresh order data",
                detail: $"Attempted: refresh all three tabs from DB. " +
                        $"Expected: updated order lists. Found: exception. " +
                        $"State: previous data may be stale.",
                ex: ex);
        }
    }

    /// <summary>
    /// Check that image files exist on disk for pending order items.
    /// Sets HasMissingFiles on orders where files are missing.
    /// </summary>
    private void VerifyPendingFiles()
    {
        foreach (var kvp in _pendingItems)
        {
            foreach (var item in kvp.Value)
            {
                // Only verify if filepath is set
                if (!string.IsNullOrEmpty(item.ImageFilepath))
                {
                    // We don't modify the item model — the tree builder checks File.Exists
                    // at build time and sets the MissingFileCount on SizeTreeItem
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Tree building
    // ══════════════════════════════════════════════════════════════════════

    private void RebuildVisibleTrees()
    {
        if (!IsLoaded) return; // guard against XAML initialization firing events early

        var selectedTab = MainTabs.SelectedIndex;

        // Always rebuild pending (it's the primary tab)
        RebuildPendingTree();

        // Only rebuild the active tab to save work
        if (selectedTab == 1)
            RebuildPrintedTree();
        else if (selectedTab == 2)
            RebuildOtherStoreTree();
    }

    private void RebuildPendingTree()
    {
        var filtered = ApplyFilters(_pendingOrders);
        var sorted = ApplySort(filtered);
        var treeItems = BuildOrderTreeItems(sorted, _pendingItems, verifyFiles: true);

        _pendingTreeItems.Clear();
        foreach (var item in treeItems)
            _pendingTreeItems.Add(item);
    }

    private void RebuildPrintedTree()
    {
        // Printed tab: no file verification at build time (lazy on click)
        var filtered = ApplyFilters(_printedOrders);
        var sorted = ApplySort(filtered);
        var treeItems = BuildOrderTreeItems(sorted, _onDemandItems, verifyFiles: false);

        _printedTreeItems.Clear();
        foreach (var item in treeItems)
            _printedTreeItems.Add(item);
    }

    private void RebuildOtherStoreTree()
    {
        var filtered = ApplyFilters(_otherStoreOrders);
        var sorted = ApplySort(filtered);
        var treeItems = BuildOrderTreeItems(sorted, _onDemandItems, verifyFiles: false);

        _otherStoreTreeItems.Clear();
        foreach (var item in treeItems)
            _otherStoreTreeItems.Add(item);
    }

    private List<OrderTreeItem> BuildOrderTreeItems(
        List<Order> orders,
        Dictionary<int, List<OrderItem>> itemsCache,
        bool verifyFiles)
    {
        var result = new List<OrderTreeItem>();

        foreach (var order in orders)
        {
            var treeItem = new OrderTreeItem
            {
                DbId = order.Id,
                ExternalOrderId = order.ExternalOrderId,
                CustomerName = order.CustomerName ?? "",
                SourceCode = order.SourceCode ?? "",
                StatusCode = order.StatusCode ?? "",
                StoreName = order.StoreName ?? "",
                OrderedAt = order.OrderedAt,
                IsHeld = order.IsHeld,
                IsTransfer = order.IsTransfer,
                Order = order
            };

            // Build size groups if items are cached
            if (itemsCache.TryGetValue(order.Id, out var items))
            {
                var groups = items
                    .GroupBy(i => new { Size = i.SizeLabel ?? "(no size)", Media = i.MediaType ?? "" })
                    .ToList();

                int totalImages = 0;
                bool hasMissing = false;

                foreach (var group in groups)
                {
                    int missingCount = 0;
                    int printedCount = 0;

                    if (verifyFiles)
                    {
                        foreach (var item in group)
                        {
                            if (!string.IsNullOrEmpty(item.ImageFilepath) && !File.Exists(item.ImageFilepath))
                                missingCount++;
                            if (item.IsPrinted)
                                printedCount++;
                        }
                    }
                    else
                    {
                        printedCount = group.Count(i => i.IsPrinted);
                    }

                    var sizeItem = new SizeTreeItem
                    {
                        SizeLabel = group.Key.Size,
                        MediaType = group.Key.Media,
                        ImageCount = group.Sum(i => i.Quantity),
                        PrintedCount = printedCount,
                        MissingFileCount = missingCount,
                        ChannelNumber = group.First().ChannelNumber,
                        Items = group.ToList()
                    };

                    treeItem.Sizes.Add(sizeItem);
                    totalImages += sizeItem.ImageCount;
                    if (missingCount > 0) hasMissing = true;
                }

                treeItem.TotalImages = totalImages;
                treeItem.HasMissingFiles = hasMissing;
                treeItem.HasUnmapped = treeItem.Sizes.Any(s => s.IsUnmapped);
            }

            result.Add(treeItem);
        }

        return result;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Filtering & sorting
    // ══════════════════════════════════════════════════════════════════════

    private List<Order> ApplyFilters(List<Order> orders)
    {
        var result = orders.AsEnumerable();

        // Source filter
        if (_sourceFilter != "All")
        {
            result = result.Where(o =>
                (o.SourceCode ?? "").Equals(_sourceFilter, StringComparison.OrdinalIgnoreCase));
        }

        // Search filter
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var search = _searchText.Trim();
            result = result.Where(o =>
                (o.ExternalOrderId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (o.CustomerName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (o.CustomerEmail?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (o.CustomerPhone?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (o.OrderNumber?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return result.ToList();
    }

    private List<Order> ApplySort(List<Order> orders)
    {
        return _sortMode switch
        {
            "Customer Name" => orders.OrderBy(o => o.CustomerName ?? "").ToList(),
            "Order ID" => orders.OrderBy(o => o.ExternalOrderId).ToList(),
            _ => orders.OrderByDescending(o => o.OrderedAt ?? o.CreatedAt).ToList() // Date Received
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Detail panel
    // ══════════════════════════════════════════════════════════════════════

    private async void ShowOrderDetail(OrderTreeItem? treeItem)
    {
        _selectedOrderItem = treeItem;

        if (treeItem?.Order == null)
        {
            DetailEmpty.Visibility = Visibility.Visible;
            DetailContent.Visibility = Visibility.Collapsed;
            return;
        }

        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailContent.Visibility = Visibility.Visible;

        var order = treeItem.Order;

        DetailOrderId.Text = order.ExternalOrderId;
        DetailStatus.Text = order.StatusCode ?? "unknown";
        DetailSource.Text = order.SourceCode ?? "unknown";
        DetailStore.Text = order.StoreName ?? "unknown";
        DetailCustomerName.Text = order.CustomerName ?? "(none)";
        DetailEmail.Text = order.CustomerEmail ?? "(none)";
        DetailPhone.Text = order.CustomerPhone ?? "(none)";
        DetailOrderedAt.Text = order.OrderedAt?.ToString("yyyy-MM-dd HH:mm") ?? "(unknown)";
        DetailTotal.Text = order.TotalAmount?.ToString("C") ?? "(none)";
        DetailFolder.Text = order.FolderPath ?? "(none)";

        // Special instructions
        if (!string.IsNullOrWhiteSpace(order.SpecialInstructions))
        {
            DetailInstructionsPanel.Visibility = Visibility.Visible;
            DetailInstructions.Text = order.SpecialInstructions;
        }
        else
        {
            DetailInstructionsPanel.Visibility = Visibility.Collapsed;
        }

        // Item count
        int itemCount = treeItem.TotalImages;
        DetailItemCount.Text = $"{itemCount}";

        // Hide size detail until a size is clicked
        SizeDetailPanel.Visibility = Visibility.Collapsed;

        // Load notes
        if (_db != null)
        {
            var notes = await _db.GetOrderNotesAsync(order.Id);
            NotesListBox.ItemsSource = notes;
        }

        // For printed/other store tabs, load items on demand if not cached
        if (!_pendingItems.ContainsKey(order.Id) && !_onDemandItems.ContainsKey(order.Id))
        {
            await LoadItemsOnDemandAsync(order.Id, treeItem);
        }

        // Load thumbnails for the order
        LoadThumbnails(treeItem);
    }

    private void LoadThumbnails(OrderTreeItem treeItem)
    {
        ThumbnailPanel.Children.Clear();

        // Collect all items across all size groups
        var allItems = treeItem.Sizes.SelectMany(s => s.Items).ToList();
        if (allItems.Count == 0) return;

        foreach (var item in allItems.Take(30)) // cap at 30 thumbnails
        {
            if (string.IsNullOrEmpty(item.ImageFilepath)) continue;

            var border = new Border
            {
                Width = 80,
                Height = 80,
                Margin = new Thickness(3),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                Background = (Brush)FindResource("SurfaceBg"),
                CornerRadius = new CornerRadius(3),
                ToolTip = item.ImageFilename ?? Path.GetFileName(item.ImageFilepath)
            };

            if (File.Exists(item.ImageFilepath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 80;
                    bmp.UriSource = new Uri(item.ImageFilepath);
                    bmp.EndInit();
                    bmp.Freeze();

                    border.Child = new Image
                    {
                        Source = bmp,
                        Stretch = Stretch.Uniform
                    };
                }
                catch
                {
                    border.Child = new TextBlock
                    {
                        Text = "?",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (Brush)FindResource("TextMuted")
                    };
                }
            }
            else
            {
                border.Child = new TextBlock
                {
                    Text = "Missing",
                    FontSize = 9,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("AccentRed")
                };
            }

            ThumbnailPanel.Children.Add(border);
        }
    }

    private async Task LoadItemsOnDemandAsync(int orderId, OrderTreeItem treeItem)
    {
        if (_db == null) return;

        try
        {
            var items = await _db.GetOrderItemsAsync(orderId);
            _onDemandItems[orderId] = items;

            // Verify files for this order (on-demand verification for printed tab)
            // Build size groups and update the tree item
            var groups = items
                .GroupBy(i => new { Size = i.SizeLabel ?? "(no size)", Media = i.MediaType ?? "" })
                .ToList();

            treeItem.Sizes.Clear();
            int totalImages = 0;
            bool hasMissing = false;

            foreach (var group in groups)
            {
                int missingCount = 0;
                int printedCount = 0;

                foreach (var item in group)
                {
                    if (!string.IsNullOrEmpty(item.ImageFilepath) && !File.Exists(item.ImageFilepath))
                        missingCount++;
                    if (item.IsPrinted)
                        printedCount++;
                }

                var sizeItem = new SizeTreeItem
                {
                    SizeLabel = group.Key.Size,
                    MediaType = group.Key.Media,
                    ImageCount = group.Sum(i => i.Quantity),
                    PrintedCount = printedCount,
                    MissingFileCount = missingCount,
                    ChannelNumber = group.First().ChannelNumber,
                    Items = group.ToList()
                };

                treeItem.Sizes.Add(sizeItem);
                totalImages += sizeItem.ImageCount;
                if (missingCount > 0) hasMissing = true;
            }

            treeItem.TotalImages = totalImages;
            treeItem.HasMissingFiles = hasMissing;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to load items on demand",
                detail: $"Attempted: load items for order {orderId} on click. " +
                        $"Expected: item list. Found: exception.",
                ex: ex);
        }
    }

    private void ShowSizeDetail(SizeTreeItem? sizeItem)
    {
        _selectedSizeItem = sizeItem;

        if (sizeItem == null)
        {
            SizeDetailPanel.Visibility = Visibility.Collapsed;
            ChannelAssignPanel.Visibility = Visibility.Collapsed;
            return;
        }

        SizeDetailPanel.Visibility = Visibility.Visible;
        SizeDetailLabel.Text = sizeItem.DisplayLabel;
        SizeDetailChannel.Text = sizeItem.ChannelLabel;
        SizeImageList.ItemsSource = sizeItem.Items;

        // Show channel assignment panel and populate combo
        ChannelAssignPanel.Visibility = Visibility.Visible;
        ChannelCombo.Items.Clear();
        foreach (var ch in _channels)
        {
            ChannelCombo.Items.Add(new ComboBoxItem
            {
                Content = $"CH {ch.ChannelNumber:D3} — {ch.SizeLabel} {ch.MediaType}",
                Tag = ch.ChannelNumber
            });
        }

        // Pre-select current channel if assigned
        if (sizeItem.ChannelNumber.HasValue && sizeItem.ChannelNumber > 0)
        {
            foreach (ComboBoxItem item in ChannelCombo.Items)
            {
                if (item.Tag is int chNum && chNum == sizeItem.ChannelNumber)
                {
                    ChannelCombo.SelectedItem = item;
                    break;
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  UI status helpers
    // ══════════════════════════════════════════════════════════════════════

    private void SetDbStatus(bool connected, string message)
    {
        DbStatusDot.Fill = connected
            ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))   // green
            : new SolidColorBrush(Color.FromRgb(0xCF, 0x44, 0x44));  // red
        DbStatusText.Text = message;
    }

    private void UpdateOrderCounts()
    {
        var pending = _pendingOrders.Count;
        var printed = _printedOrders.Count;
        var other = _otherStoreOrders.Count;
        OrderCountText.Text = $"Pending: {pending}  |  Printed: {printed}  |  Other Store: {other}";
    }

    private void DrainAlerts()
    {
        var alerts = AlertCollector.GetAndClear();
        if (alerts.Count == 0)
        {
            AlertCountText.Text = "";
            return;
        }

        AlertCountText.Text = $"Alerts: {alerts.Count}";
        AlertCountText.Visibility = Visibility.Visible;

        // Errors must be visible — show MessageBox immediately
        var errors = alerts.Where(a => a.Severity == AlertSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            var text = string.Join("\n\n", errors.Select(a => a.TechnicalDump()));
            MessageBox.Show(text, $"Errors ({errors.Count})", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Event handlers — toolbar
    // ══════════════════════════════════════════════════════════════════════

    private void SourceFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceFilterCombo.SelectedItem is ComboBoxItem item)
        {
            _sourceFilter = item.Content?.ToString() ?? "All";
            RebuildVisibleTrees();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortCombo.SelectedItem is ComboBoxItem item)
        {
            _sortMode = item.Content?.ToString() ?? "Date Received";
            RebuildVisibleTrees();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDataAsync();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_settings, _settingsManager, _db);
        settingsWindow.Owner = this;
        if (settingsWindow.ShowDialog() == true)
        {
            _settings = settingsWindow.Settings;
            ApplySettings();

            // Reconnect DB if connection settings changed
            _db = new PrintStationDb(_settings.ConnectionString);
            _refreshTimer.Interval = TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds);

            // Re-test connection
            _ = TestAndRefreshAsync();
        }
    }

    private async Task TestAndRefreshAsync()
    {
        if (_db == null) return;
        var err = await _db.TestConnectionAsync();
        if (err != null)
        {
            SetDbStatus(false, $"DB offline: {err}");
            _refreshTimer.Stop();
        }
        else
        {
            SetDbStatus(true, "Connected");
            await RefreshDataAsync();
            _refreshTimer.Start();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Event handlers — tab switching
    // ══════════════════════════════════════════════════════════════════════

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs) return;

        // Rebuild the newly selected tab
        switch (MainTabs.SelectedIndex)
        {
            case 1: RebuildPrintedTree(); break;
            case 2: RebuildOtherStoreTree(); break;
        }

        // Clear detail panel on tab switch
        ShowOrderDetail(null);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Event handlers — tree selection
    // ══════════════════════════════════════════════════════════════════════

    private void PendingTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        HandleTreeSelection(e.NewValue);
    }

    private void PrintedTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        HandleTreeSelection(e.NewValue);
    }

    private void OtherStoreTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        HandleTreeSelection(e.NewValue);
    }

    private void HandleTreeSelection(object? selectedItem)
    {
        if (selectedItem is OrderTreeItem orderItem)
        {
            ShowOrderDetail(orderItem);
            ShowSizeDetail(null);
        }
        else if (selectedItem is SizeTreeItem sizeItem)
        {
            ShowSizeDetail(sizeItem);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Event handlers — detail panel actions
    // ══════════════════════════════════════════════════════════════════════

    private async void HoldButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem?.Order == null || _db == null) return;

        var order = _selectedOrderItem.Order;
        bool newHeld = !order.IsHeld;
        string reason = newHeld ? "Placed on hold from PrintStation" : "Released from hold in PrintStation";

        bool success = await _db.ToggleHoldAsync(order.Id, newHeld, reason);
        if (success)
        {
            order.IsHeld = newHeld;
            _selectedOrderItem.IsHeld = newHeld;
            await RefreshDataAsync();
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem?.Order?.FolderPath == null) return;

        var path = _selectedOrderItem.Order.FolderPath;
        if (Directory.Exists(path))
        {
            try
            {
                Process.Start("explorer.exe", path);
            }
            catch (Exception ex)
            {
                AlertCollector.Error(AlertCategory.General,
                    "Failed to open folder",
                    detail: $"Attempted: open explorer at '{path}'. Found: exception.",
                    ex: ex);
            }
        }
        else
        {
            AlertCollector.Warn(AlertCategory.DataQuality,
                "Order folder not found on disk",
                orderId: _selectedOrderItem.Order.ExternalOrderId,
                detail: $"Attempted: open folder '{path}'. Expected: directory exists. Found: directory missing. " +
                        $"State: order {_selectedOrderItem.Order.ExternalOrderId}, store {_selectedOrderItem.Order.PickupStoreId}.");
            MessageBox.Show($"Folder not found:\n{path}", "Folder Missing",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void NotifyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem?.Order == null || _db == null) return;

        var order = _selectedOrderItem.Order;

        if (string.IsNullOrWhiteSpace(order.CustomerEmail) ||
            order.CustomerEmail.Equals("in_store@store.com", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("This customer has no email address (or it's the in-store placeholder).",
                "No Email", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // For Pixfizz orders in "Pixfizz" notify mode, mark completed via API
        bool isPixfizz = (order.SourceCode ?? "").Equals("pixfizz", StringComparison.OrdinalIgnoreCase);
        if (isPixfizz && _settings.PixfizzNotifyMode == "Pixfizz" && !string.IsNullOrEmpty(order.PixfizzOrderId))
        {
            if (!string.IsNullOrEmpty(_settings.PixfizzApiKey))
            {
                var notifier = new PixfizzNotifier(_settings.PixfizzApiKey, _settings.PixfizzApiUrl,
                    _settings.PixfizzOrganizationId, _settings.PixfizzLocationId);
                bool ok = await notifier.MarkCompletedAsync(order.PixfizzOrderId);
                if (ok)
                {
                    await _db.UpdateOrderStatusAsync(order.Id, 5, notes: "Pixfizz mark-completed sent");
                    await _db.AddNoteAsync(order.Id, null, "Pixfizz notification sent", "notification");
                    await RefreshDataAsync();
                    MessageBox.Show("Pixfizz notified — customer will receive email from Pixfizz.",
                        "Notified", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Failed to mark completed in Pixfizz. Check alerts.",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
            }
        }

        // Email notification
        var emailSvc = new EmailService(_settings);
        var result = await emailSvc.SendOrderReadyEmailAsync(order);
        if (result.Success)
        {
            await _db.UpdateOrderStatusAsync(order.Id, 5, notes: "Email notification sent");
            await _db.AddNoteAsync(order.Id, null, $"Email sent to {order.CustomerEmail}", "notification");
            await RefreshDataAsync();
            MessageBox.Show($"Email sent to {order.CustomerEmail}.",
                "Notified", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"Email failed: {result.ErrorMessage}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void TransferButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem?.Order == null || _db == null) return;

        var order = _selectedOrderItem.Order;

        // Determine target store (the other one)
        var stores = await _db.GetStoresAsync();
        var otherStores = stores.Where(s => s.Id != _settings.StoreId).ToList();

        if (otherStores.Count == 0)
        {
            MessageBox.Show("No other stores configured.", "Transfer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // For now pick the first other store (2-store system)
        var target = otherStores[0];
        var confirm = MessageBox.Show(
            $"Transfer order {order.ExternalOrderId} to {target.StoreName}?\n\n" +
            "This updates the DB only — image files must be transferred separately (SFTP).",
            "Confirm Transfer", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        bool ok = await _db.TransferOrderAsync(order.Id, target.Id,
            $"Transferred from PrintStation to {target.StoreName}");

        if (ok)
        {
            await RefreshDataAsync();
            MessageBox.Show($"Order transferred to {target.StoreName}.",
                "Transfer Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ColorCorrectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem?.Order == null) return;
        if (_selectedSizeItem == null || _selectedSizeItem.Items.Count == 0)
        {
            MessageBox.Show("Select a size group first.", "No Size Selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var order = _selectedOrderItem.Order;
        var imagePaths = _selectedSizeItem.Items
            .Where(i => !string.IsNullOrEmpty(i.ImageFilepath) && File.Exists(i.ImageFilepath))
            .Select(i => i.ImageFilepath!)
            .ToList();

        if (imagePaths.Count == 0)
        {
            MessageBox.Show("No image files found on disk for this size.", "No Images",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var correctionStore = new CorrectionStore();
        var vm = new ColorCorrectWindowViewModel(
            order.ExternalOrderId,
            order.FolderPath ?? Path.GetDirectoryName(imagePaths[0])!,
            _selectedSizeItem.DisplayLabel,
            imagePaths,
            correctionStore,
            _settings,
            confirmedStates => { /* corrections saved by VM */ });

        var ccWindow = new ColorCorrectWindow { DataContext = vm, Owner = this };
        ccWindow.ShowDialog();
    }

    private void ChangeSizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem?.Order == null) return;
        if (_selectedSizeItem == null || _selectedSizeItem.Items.Count == 0)
        {
            MessageBox.Show("Select a size group first.", "No Size Selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var csWindow = new ChangeSizeWindow(
            _selectedSizeItem.SizeLabel,
            _selectedOrderItem.Order,
            _selectedSizeItem.Items,
            _channels,
            _settings,
            _db);
        csWindow.Owner = this;
        csWindow.ShowDialog();

        // Refresh after potential print from ChangeSizeWindow
        _ = RefreshDataAsync();
    }

    private void AlertCountText_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Drain alerts and show in a message box for now
        // Full alert window will come in Phase 5
        var alerts = AlertCollector.GetAndClear();
        if (alerts.Count == 0) return;

        var text = string.Join("\n\n", alerts.Select(a => a.TechnicalDump()));
        MessageBox.Show(text, $"Alerts ({alerts.Count})", MessageBoxButton.OK, MessageBoxImage.Information);
        DrainAlerts();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Channel assignment
    // ══════════════════════════════════════════════════════════════════════

    private async void AssignChannelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSizeItem == null || _selectedOrderItem == null || _db == null) return;

        // Get selected channel number
        int channelNumber = 0;
        if (ChannelCombo.SelectedItem is ComboBoxItem comboItem && comboItem.Tag is int chNum)
        {
            channelNumber = chNum;
        }
        else if (int.TryParse(ChannelCombo.Text.Trim(), out int typed) && typed > 0)
        {
            channelNumber = typed;
        }

        if (channelNumber == 0)
        {
            MessageBox.Show("Select or type a channel number.", "No Channel",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Update all items in this size group in the DB
        int updated = 0;
        foreach (var item in _selectedSizeItem.Items)
        {
            if (await _db.UpdateItemChannelAsync(item.Id, channelNumber))
            {
                item.ChannelNumber = channelNumber;
                updated++;
            }
        }

        if (updated > 0)
        {
            _selectedSizeItem.ChannelNumber = channelNumber;
            SizeDetailChannel.Text = _selectedSizeItem.ChannelLabel;

            // Save to routing map for future auto-assignment
            string routingKey = $"size={_selectedSizeItem.SizeLabel}|media={_selectedSizeItem.MediaType}".ToLowerInvariant();
            _settings.RoutingMap[routingKey] = new RoutingEntry
            {
                ChannelNumber = channelNumber,
                Source = _selectedOrderItem.SourceCode
            };
            _settingsManager.Save(_settings);

            AlertCollector.Info(AlertCategory.Printing,
                $"Channel {channelNumber:D3} assigned to {_selectedSizeItem.DisplayLabel}",
                orderId: _selectedOrderItem.ExternalOrderId,
                detail: $"Updated {updated} items in DB.");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Printing
    // ══════════════════════════════════════════════════════════════════════

    private async void PrintSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem?.Order == null || _db == null) return;

        if (string.IsNullOrEmpty(_settings.NoritsuOutputRoot))
        {
            MessageBox.Show("Noritsu output root is not configured.\nSet it in Settings.",
                "Not Configured", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!Directory.Exists(_settings.NoritsuOutputRoot))
        {
            MessageBox.Show($"Noritsu output folder not found:\n{_settings.NoritsuOutputRoot}",
                "Folder Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_settings.DeveloperMode)
        {
            MessageBox.Show("Developer mode is ON — Noritsu output is blocked.\nDisable in Settings to print.",
                "Dev Mode", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var order = _selectedOrderItem.Order;

        // Collect all size groups that have a channel assigned
        var sizesToPrint = _selectedOrderItem.Sizes
            .Where(s => s.ChannelNumber.HasValue && s.ChannelNumber > 0 && !s.IsPrinted)
            .ToList();

        if (sizesToPrint.Count == 0)
        {
            MessageBox.Show("No unmapped sizes to print.\nAssign channels first, or all sizes are already printed.",
                "Nothing to Print", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Confirm
        var sizeList = string.Join("\n", sizesToPrint.Select(s =>
            $"  {s.DisplayLabel} — CH {s.ChannelNumber:D3} — {s.ImageCount} images"));
        var confirm = MessageBox.Show(
            $"Print order {order.ExternalOrderId}?\n\n{sizeList}",
            "Confirm Print", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        var writer = new NoritsuMrkWriter(_settings.NoritsuOutputRoot);
        int printedSizes = 0;

        foreach (var sizeItem in sizesToPrint)
        {
            try
            {
                writer.WriteMrk(order, sizeItem.SizeLabel, sizeItem.ChannelNumber!.Value, sizeItem.Items);

                // Mark items as printed in DB
                var now = DateTime.Now;
                foreach (var item in sizeItem.Items)
                {
                    await _db.UpdateItemPrintedAsync(item.Id, now);
                    item.IsPrinted = true;
                    item.PrintedAt = now;
                }

                sizeItem.PrintedCount = sizeItem.Items.Count;
                printedSizes++;

                AlertCollector.Info(AlertCategory.Printing,
                    $"MRK written for {sizeItem.DisplayLabel}",
                    orderId: order.ExternalOrderId,
                    detail: $"Channel {sizeItem.ChannelNumber:D3}, {sizeItem.ImageCount} images.");
            }
            catch (Exception ex)
            {
                AlertCollector.Error(AlertCategory.Printing,
                    $"Failed to write MRK for {sizeItem.DisplayLabel}",
                    orderId: order.ExternalOrderId,
                    detail: $"Attempted: write Noritsu folder for {sizeItem.DisplayLabel} on CH {sizeItem.ChannelNumber}. " +
                            $"Expected: MRK written + images copied. Found: exception. " +
                            $"State: order {order.ExternalOrderId}, {sizeItem.Items.Count} items, output root '{_settings.NoritsuOutputRoot}'.",
                    ex: ex);
            }
        }

        if (printedSizes > 0)
        {
            // Update order status to ready (4) if all sizes printed
            bool allPrinted = _selectedOrderItem.Sizes.All(s => s.IsPrinted);
            if (allPrinted)
            {
                await _db.UpdateOrderStatusAsync(order.Id, 4, notes: "All sizes printed from PrintStation");
            }
            else
            {
                // At least some printing happened — set to in_progress (2)
                if (order.OrderStatusId == 1)
                    await _db.UpdateOrderStatusAsync(order.Id, 2, notes: "Partial print from PrintStation");
            }

            await RefreshDataAsync();
            MessageBox.Show($"Printed {printedSizes} size(s) for order {order.ExternalOrderId}.",
                "Print Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}

// ══════════════════════════════════════════════════════════════════════
//  Value converters
// ══════════════════════════════════════════════════════════════════════

public class BoolToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter,
        System.Globalization.CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter,
        System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

public class StringToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public static readonly StringToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter,
        System.Globalization.CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter,
        System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
