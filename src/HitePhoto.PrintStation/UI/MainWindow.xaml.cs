using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Core.Models;
using HitePhoto.PrintStation.Core.Processing;
using HitePhoto.PrintStation.Data;
using HitePhoto.PrintStation.Data.Repositories;
using HitePhoto.PrintStation.Data.Sync;
using HitePhoto.PrintStation.UI.ViewModels;

namespace HitePhoto.PrintStation.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly SettingsManager _settingsManager;
    private readonly Data.Repositories.IOrderRepository _orders;
    private readonly Data.CorrectionStore _correctionStore;
    private readonly IAlertRepository _alertRepo;
    private readonly Core.Services.INotificationService _notificationService;
    private readonly Data.Repositories.IOptionDefaultsRepository _optionDefaults;

    private readonly System.Collections.ObjectModel.ObservableCollection<AlertItemViewModel> _sessionAlerts = new();
    private int _nextAlertId;

    // Timers
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _verifyTimer;
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _alertDrainTimer;

    private bool _refreshInProgress;

    // Cancellation for async ingest
    private CancellationTokenSource _ingestCts = new();

    // Click-verify: debounce + background thread
    private readonly DispatcherTimer _verifyDebounce;
    private OrderTreeItem? _pendingVerifyOrder;
    private CancellationTokenSource _verifyCts = new();
    private CancellationTokenSource _thumbnailCts = new();

    // Currently selected (for detail panel)
    private OrderTreeItem? _selectedOrderItem;
    private SizeTreeItem? _selectedSizeItem;
    private OrderTreeItem? _lastClickedOrder; // anchor for shift-select
    private SizeTreeItem? _lastClickedSize;

    public MainWindow(MainViewModel vm, AppSettings settings, SettingsManager settingsManager,
        Data.Repositories.IOrderRepository orders, Data.CorrectionStore correctionStore,
        IAlertRepository alertRepo, Core.Services.INotificationService notificationService,
        Data.Repositories.IOptionDefaultsRepository optionDefaults)
    {
        InitializeComponent();

        Title = $"HitePhoto Print Station — build {BuildInfo.BuildTimestamp}";

        _vm = vm;
        _settings = settings;
        _settingsManager = settingsManager;
        _orders = orders;
        _correctionStore = correctionStore;
        _alertRepo = alertRepo;
        _notificationService = notificationService;
        _optionDefaults = optionDefaults;

        PendingTree.ItemsSource = _vm.PendingOrders;
        PrintedTree.ItemsSource = _vm.PrintedOrders;
        OtherStoreTree.ItemsSource = _vm.OtherStoreOrders;

        // Refresh timer — sync → scan → poll → load
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds) };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (_refreshInProgress) return;
            _refreshInProgress = true;
            try { await RunFullRefreshAsync(); }
            finally { _refreshInProgress = false; }
        };

        // Hourly verify — full disk-vs-DB check + self-healing timestamps
        _verifyTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _verifyTimer.Tick += async (_, _) =>
        {
            await Task.Run(() => _vm.VerifyRecentOrders(_settings.DaysToVerify));
            _vm.LoadOrders();
            UpdateStatusBar();
        };

        // Verify debounce — wait 300ms after last click before running verify on background thread
        _verifyDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _verifyDebounce.Tick += (_, _) =>
        {
            _verifyDebounce.Stop();
            var order = _pendingVerifyOrder;
            if (order == null) return;

            _verifyCts.Cancel();
            _verifyCts = new CancellationTokenSource();
            var ct = _verifyCts.Token;

            Task.Run(() =>
            {
                if (ct.IsCancellationRequested) return;
                _vm.VerifyOrder(order);
                if (_vm.NeedsRefresh)
                {
                    Dispatcher.Invoke(() =>
                    {
                        _vm.LoadOrders();
                        UpdateStatusBar();
                        _vm.NeedsRefresh = false;
                    });
                }
            }, ct);
        };

        // Search debounce
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _vm.LoadOrders();
            UpdateStatusBar();
        };

        // Alert drain
        _alertDrainTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _alertDrainTimer.Tick += (_, _) => DrainAlerts();
        _alertDrainTimer.Start();

        AlertList.ItemsSource = _sessionAlerts;

        Loaded += MainWindow_Loaded;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Startup
    // ══════════════════════════════════════════════════════════════════════

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySettings();

        // Step 4: Show tree immediately from what's already in SQLite (fast, no disk I/O)
        _vm.LoadOrders();
        UpdateStatusBar();

        // Step 7: Start timers (0 = disabled)
        if (_settings.RefreshIntervalSeconds > 0)
            _refreshTimer.Start();

        // Step 8: Kick off background verify (discovers missing orders + validates existing)
        Task.Run(() =>
        {
            _vm.VerifyRecentOrders(_settings.DaysToVerify);

            // If verify inserted/repaired anything, refresh tree on UI thread
            if (_vm.NeedsRefresh)
            {
                Dispatcher.Invoke(() =>
                {
                    _vm.LoadOrders();
                    UpdateStatusBar();
                    _vm.NeedsRefresh = false;
                });
            }

            // Enable ingest timers now that initial verify is done
            Dispatcher.Invoke(() =>
            {
                if (_settings.DakisEnabled && !string.IsNullOrWhiteSpace(_settings.DakisWatchFolder))
                    _vm.StartDakisWatcher();

                _verifyTimer.Start();
                _ = Core.AutoUpdater.CheckAndPromptAsync(_settings);
            });
        });
    }

    private void ApplySettings()
    {
        AppLog.Enabled = _settings.EnableLogging;

        var themeName = _settings.Theme.Equals("Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        var themeUri = new Uri($"pack://application:,,,/UI/Themes/{themeName}.xaml", UriKind.Absolute);
        Application.Current.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = themeUri });

        DevModeText.Visibility = _settings.DeveloperMode ? Visibility.Visible : Visibility.Collapsed;
        var devTag = _settings.DeveloperMode ? " [DEV MODE]" : "";
        Title = $"HitePhoto Print Station — build {BuildInfo.BuildTimestamp}{devTag}";
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Status bar
    // ══════════════════════════════════════════════════════════════════════

    private void UpdateStatusBar()
    {
        DbStatusDot.Fill = _vm.IsDbConnected
            ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
            : new SolidColorBrush(Color.FromRgb(0xCF, 0x44, 0x44));
        DbStatusText.Text = _vm.IsDbConnected ? "SQLite OK" : "SQLite error";
        OrderCountText.Text = _vm.StatusText;
        UpdateTabHeaders();
    }

    private void UpdateTabHeaders()
    {
        var hasSearch = !string.IsNullOrWhiteSpace(_vm.SearchText);
        var pc = _vm.PendingOrders.Count;
        var prc = _vm.PrintedOrders.Count;
        var oc = _vm.OtherStoreOrders.Count;

        PendingTab.Header = hasSearch && pc > 0 ? $"Pending Orders ({pc})" : "Pending Orders";
        PrintedTab.Header = hasSearch && prc > 0 ? $"Printed Orders ({prc})" : "Printed Orders";
        OtherStoreTab.Header = hasSearch && oc > 0 ? $"Other Store ({oc})" : "Other Store";
    }

    private void DrainAlerts()
    {
        var drained = AlertCollector.GetAndClear();
        if (drained.Count == 0) return;

        var errorBrush = (Brush)FindResource("AccentRed");
        var warnBrush = (Brush)FindResource("AccentOrange");
        var errorBg = (Brush)FindResource("AlertErrorBg");
        var warnBg = (Brush)FindResource("AlertWarnBg");

        bool hasErrors = false;
        foreach (var a in drained)
        {
            if (a.Severity == AlertSeverity.Error) hasErrors = true;

            _sessionAlerts.Insert(0, new AlertItemViewModel
            {
                Id = _nextAlertId++,
                SeverityLabel = a.SeverityLabel,
                Summary = a.Summary,
                OrderId = a.OrderId,
                TimestampText = a.Timestamp.ToString("HH:mm:ss"),
                TechnicalDetail = a.TechnicalDump(),
                SeverityBrush = a.Severity == AlertSeverity.Error ? errorBrush : warnBrush,
                BackgroundBrush = a.Severity == AlertSeverity.Error ? errorBg : warnBg
            });
        }

        UpdateAlertPanel();

        if (hasErrors)
            AlertPanel.Visibility = Visibility.Visible;
    }

    private void UpdateAlertPanel()
    {
        AlertHeaderText.Text = $"Alerts ({_sessionAlerts.Count})";

        if (_sessionAlerts.Count > 0)
        {
            AlertBadge.Visibility = Visibility.Visible;
            AlertBadgeText.Text = _sessionAlerts.Count.ToString();
        }
        else
        {
            AlertBadge.Visibility = Visibility.Collapsed;
            AlertPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void AlertBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        AlertPanel.Visibility = AlertPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void DismissAlert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int index)
        {
            var item = _sessionAlerts.FirstOrDefault(a => a.Id == index);
            if (item != null)
                _sessionAlerts.Remove(item);
            UpdateAlertPanel();
        }
    }

    private void DismissAllAlerts_Click(object sender, RoutedEventArgs e)
    {
        _sessionAlerts.Clear();
        UpdateAlertPanel();
    }

    private void CollapseAlertPanel_Click(object sender, RoutedEventArgs e)
    {
        AlertPanel.Visibility = Visibility.Collapsed;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Tree selection
    // ══════════════════════════════════════════════════════════════════════

    private void Tree_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Close channel popup when clicking anywhere in the tree
        if (ChannelPopup.IsOpen) ChannelPopup.IsOpen = false;

        // Find the data item that was clicked
        var hit = e.OriginalSource as DependencyObject;
        object? dataItem = null;
        while (hit != null)
        {
            if (hit is FrameworkElement fe && fe.DataContext is OrderTreeItem or SizeTreeItem)
            {
                dataItem = fe.DataContext;
                break;
            }
            hit = VisualTreeHelper.GetParent(hit);
        }
        if (dataItem == null) return;

        // Don't intercept expander arrow clicks
        var expanderHit = e.OriginalSource as DependencyObject;
        while (expanderHit != null)
        {
            if (expanderHit is System.Windows.Controls.Primitives.ToggleButton) return;
            expanderHit = VisualTreeHelper.GetParent(expanderHit);
        }

        // Determine which collection this tree belongs to
        var collection = sender == PendingTree ? _vm.PendingOrders
                       : sender == PrintedTree ? _vm.PrintedOrders
                       : _vm.OtherStoreOrders;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (dataItem is OrderTreeItem orderItem)
        {
            if (ctrl)
            {
                orderItem.IsSelected = !orderItem.IsSelected;
                if (orderItem.IsSelected)
                    _lastClickedOrder = orderItem;
            }
            else if (shift && _lastClickedOrder != null)
            {
                int from = collection.IndexOf(_lastClickedOrder);
                int to = collection.IndexOf(orderItem);
                if (from >= 0 && to >= 0)
                {
                    ClearAllSelections();
                    int lo = Math.Min(from, to);
                    int hi = Math.Max(from, to);
                    for (int i = lo; i <= hi; i++)
                        collection[i].IsSelected = true;
                }
            }
            else
            {
                ClearAllSelections();
                orderItem.IsSelected = true;
                _lastClickedOrder = orderItem;
            }

            _selectedOrderItem = orderItem;
            _vm.SelectedOrder = orderItem;

            var selectedOrders = GetSelectedOrders();
            if (selectedOrders.Count > 1)
            {
                ShowMultiSelectMessage(selectedOrders.Count);
            }
            else
            {
                _pendingVerifyOrder = orderItem;
                _verifyDebounce.Stop();
                _verifyDebounce.Start();
                ShowOrderDetail(orderItem);
            }
            ShowSizeDetail(null);
            e.Handled = true;
        }
        else if (dataItem is SizeTreeItem sizeItem)
        {
            if (ctrl)
            {
                sizeItem.IsSelected = !sizeItem.IsSelected;
                if (sizeItem.IsSelected)
                    _lastClickedSize = sizeItem;
            }
            else if (shift && _lastClickedSize != null
                     && sizeItem.ParentOrder != null
                     && _lastClickedSize.ParentOrder == sizeItem.ParentOrder)
            {
                var sizes = sizeItem.ParentOrder.Sizes;
                int from = sizes.IndexOf(_lastClickedSize);
                int to = sizes.IndexOf(sizeItem);
                if (from >= 0 && to >= 0)
                {
                    ClearAllSelections();
                    int lo = Math.Min(from, to);
                    int hi = Math.Max(from, to);
                    for (int i = lo; i <= hi; i++)
                        sizes[i].IsSelected = true;
                }
            }
            else
            {
                ClearAllSelections();
                sizeItem.IsSelected = true;
                _lastClickedSize = sizeItem;
            }

            _selectedSizeItem = sizeItem;

            if (sizeItem.ParentOrder != null)
            {
                _lastClickedOrder = sizeItem.ParentOrder;

                if (_selectedOrderItem?.DbId != sizeItem.ParentOrder.DbId)
                {
                    _selectedOrderItem = sizeItem.ParentOrder;
                    _vm.SelectedOrder = sizeItem.ParentOrder;
                    _pendingVerifyOrder = sizeItem.ParentOrder;
                    _verifyDebounce.Stop();
                    _verifyDebounce.Start();
                }
                ShowOrderDetail(sizeItem.ParentOrder);
            }

            var selectedSizes = GetSelectedSizes();
            if (selectedSizes.Count > 1)
                ShowMultiSelectMessage(selectedSizes.Count, isSizes: true);
            else
                ShowSizeDetail(sizeItem);

            LoadSizeThumbnails(sizeItem);
            e.Handled = true;
        }
    }

    private void ClearAllSelections()
    {
        foreach (var o in _vm.PendingOrders)
        {
            o.IsSelected = false;
            foreach (var s in o.Sizes) s.IsSelected = false;
        }
        foreach (var o in _vm.PrintedOrders)
        {
            o.IsSelected = false;
            foreach (var s in o.Sizes) s.IsSelected = false;
        }
        foreach (var o in _vm.OtherStoreOrders)
        {
            o.IsSelected = false;
            foreach (var s in o.Sizes) s.IsSelected = false;
        }
    }

    private List<OrderTreeItem> GetSelectedOrders()
    {
        var selected = new List<OrderTreeItem>();
        selected.AddRange(_vm.PendingOrders.Where(o => o.IsSelected));
        selected.AddRange(_vm.PrintedOrders.Where(o => o.IsSelected));
        selected.AddRange(_vm.OtherStoreOrders.Where(o => o.IsSelected));
        return selected;
    }

    private List<SizeTreeItem> GetSelectedSizes()
    {
        var selected = new List<SizeTreeItem>();
        foreach (var o in _vm.PendingOrders)
            selected.AddRange(o.Sizes.Where(s => s.IsSelected));
        foreach (var o in _vm.PrintedOrders)
            selected.AddRange(o.Sizes.Where(s => s.IsSelected));
        foreach (var o in _vm.OtherStoreOrders)
            selected.AddRange(o.Sizes.Where(s => s.IsSelected));
        return selected;
    }

    private void ShowMultiSelectMessage(int count, bool isSizes = false)
    {
        DetailEmpty.Text = isSizes ? $"{count} sizes selected" : $"{count} orders selected";
        DetailEmpty.Visibility = Visibility.Visible;
        DetailContent.Visibility = Visibility.Collapsed;
        SizeDetailPanel.Visibility = Visibility.Collapsed;
        ThumbnailPanel.Children.Clear();
        NotesPreview.ItemsSource = null;
        HistoryButton.Visibility = Visibility.Collapsed;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Detail panel
    // ══════════════════════════════════════════════════════════════════════

    private void ShowOrderDetail(OrderTreeItem? treeItem)
    {
        if (treeItem == null)
        {
            DetailEmpty.Text = "Select an order or size";
            DetailEmpty.Visibility = Visibility.Visible;
            DetailContent.Visibility = Visibility.Collapsed;
            return;
        }

        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailContent.Visibility = Visibility.Visible;

        DetailOrderId.Text = treeItem.ShortId;
        DetailCustomerName.Text = treeItem.CustomerName;
        DetailPhone.Text = FormatPhone(treeItem.CustomerPhone);
        DetailEmail.Text = treeItem.CustomerEmail;
        DetailStatus.Text = treeItem.StatusCode;
        DetailSource.Text = treeItem.SourceCode;
        DetailStore.Text = treeItem.StoreName;
        DetailOrderedAt.Text = treeItem.OrderedAt?.ToString("yyyy-MM-dd HH:mm") ?? "";
        DetailItemCount.Text = $"{treeItem.TotalImages}";

        // Order options (Glossy, Matte, etc.)
        LoadOrderOptions(treeItem);

        // Notes — show last 2 in preview, button for full history
        var allNotes = _vm.OrderNotes;
        NotesPreview.ItemsSource = allNotes.Take(2).ToList();
        HistoryButton.Visibility = allNotes.Count > 2 ? Visibility.Visible : Visibility.Collapsed;

        SizeDetailPanel.Visibility = Visibility.Collapsed;

        LoadThumbnails(treeItem);
    }

    private void ShowSizeDetail(SizeTreeItem? sizeItem)
    {
        if (sizeItem == null)
        {
            SizeDetailPanel.Visibility = Visibility.Collapsed;
            ChannelPopup.IsOpen = false;
            return;
        }

        SizeDetailPanel.Visibility = Visibility.Visible;
        SizeDetailLabel.Text = sizeItem.DisplayLabel;
        SizeDetailChannel.Text = sizeItem.ChannelLabel;
        ChannelSearchBox.Text = "";
        ChannelPopup.IsOpen = false;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Channel assignment
    // ══════════════════════════════════════════════════════════════════════

    private List<(string Display, int Channel, string? LayoutName)> _channelEntries = new();
    private List<(string Display, int Channel, string? LayoutName)> _filteredChannelEntries = new();
    private bool _channelEntriesLoaded;

    private void LoadChannelEntries()
    {
        _channelEntries.Clear();

        // Special options first
        _channelEntries.Add(("— Unassigned —", 0, null));
        _channelEntries.Add(("— Skip (no print) —", -1, null));

        // Layouts
        foreach (var l in _settings.Layouts)
        {
            _channelEntries.Add((
                $"[Layout] {l.Name}  ({l.PrintSizeDisplay} {l.GridDisplay} → CH {l.TargetChannelNumber:D3})",
                l.TargetChannelNumber, l.Name));
        }

        // Channels from Noritsu CSV
        if (!string.IsNullOrWhiteSpace(_settings.ChannelsCsvPath))
        {
            var reader = new Core.Processing.ChannelsCsvReader(_settings.ChannelsCsvPath);
            foreach (var c in reader.Load())
            {
                _channelEntries.Add((
                    $"{c.ChannelNumber:D3}  {c.SizeLabel}  {c.MediaType}  {c.Description}".Trim(),
                    c.ChannelNumber, null));
            }
        }

        // Existing mappings from DB (in case CSV doesn't cover everything)
        var existingNumbers = new HashSet<int>(_channelEntries.Select(e => e.Channel));
        foreach (var c in _vm.GetAllChannels())
        {
            if (!existingNumbers.Contains(c.ChannelNumber))
            {
                _channelEntries.Add((
                    $"{c.ChannelNumber:D3}  {c.SizeLabel}  {c.MediaType}  {c.Description}".Trim(),
                    c.ChannelNumber, null));
            }
        }

        _channelEntriesLoaded = true;
    }

    private void PopulateChannelList(string filter = "")
    {
        _filteredChannelEntries = string.IsNullOrWhiteSpace(filter)
            ? _channelEntries
            : _channelEntries.Where(e =>
                e.Display.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        ChannelList.ItemsSource = _filteredChannelEntries.Select(e => e.Display).ToList();
    }

    private void ChannelSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_channelEntriesLoaded) LoadChannelEntries();
        PopulateChannelList(ChannelSearchBox.Text.Trim());
        ChannelPopup.IsOpen = true;
    }

    private void ChannelSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (!_channelEntriesLoaded) LoadChannelEntries();
        PopulateChannelList();
        ChannelPopup.IsOpen = true;
    }


    private void ChannelList_MouseClick(object sender, MouseButtonEventArgs e)
    {
        if (ChannelList.SelectedIndex >= 0)
            AssignChannel_Click(sender, e);
    }

    private void AssignChannel_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSizeItem == null || ChannelList.SelectedIndex < 0) return;

        var selected = _filteredChannelEntries[ChannelList.SelectedIndex];

        if (selected.Channel == 0)
        {
            // Unassign — delete the mapping
            _vm.UnassignChannel(_selectedSizeItem.SizeLabel, _selectedSizeItem.MediaType);
        }
        else
        {
            _vm.AssignChannel(_selectedSizeItem.SizeLabel, _selectedSizeItem.MediaType,
                selected.Channel, selected.LayoutName);
        }

        _selectedSizeItem.ChannelNumber = selected.Channel;
        SizeDetailChannel.Text = _selectedSizeItem.ChannelLabel;
        ChannelPopup.IsOpen = false;
        ChannelSearchBox.Text = "";

        _channelEntriesLoaded = false;
        _vm.LoadOrders();
        UpdateStatusBar();
    }

    private void LoadOrderOptions(OrderTreeItem treeItem)
    {
        // Options are stored as JSON on each item but are order-level for Dakis prints.
        // Grab from the first item that has options_json.
        var firstItem = treeItem.Sizes
            .SelectMany(s => s.Items)
            .FirstOrDefault(i => !string.IsNullOrEmpty(i.OptionsJson) && i.OptionsJson != "[]");

        if (firstItem == null)
        {
            OptionsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var options = System.Text.Json.JsonSerializer.Deserialize<List<HitePhoto.Shared.Parsers.OrderItemOption>>(
                firstItem.OptionsJson!);
            if (options == null || options.Count == 0)
            {
                OptionsPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var defaults = _optionDefaults.GetAll();
            OptionsList.ItemsSource = options.Select(o =>
                new ViewModels.OptionBadge(o.Key, o.Value, defaults.Contains((o.Key, o.Value)))).ToList();
            OptionsPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to load order options",
                detail: $"Attempted: deserialize options JSON for order {treeItem.ShortId}. " +
                        $"Expected: list of OrderItemOption. Found: {ex.GetType().Name}: {ex.Message}. " +
                        $"Context: LoadOrderOptions. State: OptionsJson='{firstItem.OptionsJson}'.",
                ex: ex);
            OptionsPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OptionBadge_ToggleDefault_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        if (mi.DataContext is not ViewModels.OptionBadge badge) return;

        try
        {
            if (badge.IsDefault)
                _optionDefaults.RemoveDefault(badge.OptionKey, badge.OptionValue);
            else
                _optionDefaults.SetDefault(badge.OptionKey, badge.OptionValue);

            // Rebuild the options display to reflect the change
            if (_selectedOrderItem != null)
                LoadOrderOptions(_selectedOrderItem);
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to toggle option default",
                detail: $"Attempted: toggle default for '{badge.OptionKey}: {badge.OptionValue}'. " +
                        $"Expected: row inserted/deleted in option_defaults. Found: {ex.GetType().Name}: {ex.Message}. " +
                        $"Context: OptionBadge_ToggleDefault_Click. State: IsDefault was {badge.IsDefault}.",
                ex: ex);
        }
    }

    private void LoadThumbnails(OrderTreeItem treeItem)
        => LoadThumbnailsFromItems(treeItem.Sizes.SelectMany(s => s.Items));

    private void LoadSizeThumbnails(SizeTreeItem sizeItem)
        => LoadThumbnailsFromItems(sizeItem.Items);

    private void LoadThumbnailsFromItems(IEnumerable<HitePhoto.Shared.Models.OrderItem> items)
    {
        _thumbnailCts.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        var ct = _thumbnailCts.Token;

        ThumbnailPanel.Children.Clear();

        // Snapshot styles on UI thread
        var borderBrush = (Brush)FindResource("BorderBrush");
        var surfaceBg = (Brush)FindResource("SurfaceBg");
        var mutedBrush = (Brush)FindResource("TextMuted");
        var redBrush = (Brush)FindResource("AccentRed");

        // Create placeholder borders immediately, then load images in background
        var itemList = items.Take(30).Where(i => !string.IsNullOrEmpty(i.ImageFilepath)).ToList();

        foreach (var item in itemList)
        {
            var border = new Border
            {
                Width = 110, Height = 110,
                Margin = new Thickness(3),
                BorderThickness = new Thickness(1),
                BorderBrush = borderBrush,
                Background = surfaceBg,
                CornerRadius = new CornerRadius(3),
                ToolTip = item.ImageFilename ?? Path.GetFileName(item.ImageFilepath)
            };
            ThumbnailPanel.Children.Add(border);
        }

        // Load images on background thread, push each to UI as it loads
        var paths = itemList.Select(i => i.ImageFilepath!).ToList();
        Task.Run(() =>
        {
            for (int i = 0; i < paths.Count; i++)
            {
                if (ct.IsCancellationRequested) return;

                var path = paths[i];
                var index = i;
                BitmapImage? bmp = null;
                bool exists = File.Exists(path);

                if (exists)
                {
                    try
                    {
                        bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = 110;
                        bmp.UriSource = new Uri(path);
                        bmp.EndInit();
                        bmp.Freeze();
                    }
                    catch { bmp = null; }
                }

                if (ct.IsCancellationRequested) return;

                Dispatcher.BeginInvoke(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    if (index >= ThumbnailPanel.Children.Count) return;
                    var border = (Border)ThumbnailPanel.Children[index];

                    if (bmp != null)
                        border.Child = new Image { Source = bmp, Stretch = Stretch.Uniform };
                    else if (!exists)
                        border.Child = new TextBlock
                        {
                            Text = "Missing", FontSize = 9,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = redBrush
                        };
                    else
                        border.Child = new TextBlock
                        {
                            Text = "?",
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = mutedBrush
                        };
                });
            }
        }, ct);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Toolbar event handlers
    // ══════════════════════════════════════════════════════════════════════

    private void SourceFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null) return; // fires during XAML init before DI assigns _vm
        if (SourceFilterCombo.SelectedItem is ComboBoxItem item)
        {
            _vm.SourceFilter = item.Content?.ToString() ?? "All";
            _vm.LoadOrders();
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_vm == null) return;
        _vm.SearchText = SearchBox.Text;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null) return;
        if (SortCombo.SelectedItem is ComboBoxItem item)
        {
            _vm.SortMode = item.Content?.ToString() ?? "Date Received";
            _vm.LoadOrders();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RunFullRefreshAsync(includeVerify: true);
    }

    private async Task RunFullRefreshAsync(bool includeVerify = false)
    {
        try
        {
            // 1. Sync pull (MariaDB → SQLite)
            if (_settings.SyncEnabled)
            {
                var sync = App.Services.GetRequiredService<Data.Sync.ISyncService>();
                await Task.Run(async () =>
                {
                    await sync.PullAsync();
                    await sync.ProcessOutboxAsync();
                });
            }

            // 2. Dakis scan
            await Task.Run(() => _vm.RunDakisScan());

            // 3. Pixfizz poll
            await _vm.RunPixfizzPollAsync(_ingestCts.Token);

            // 4. Verify (only on button click, new orders, or hourly — not every 30s)
            if (includeVerify)
                await Task.Run(() => _vm.VerifyRecentOrders(_settings.DaysToVerify));

            // 5. Load from SQLite + display
            _vm.LoadOrders();
            UpdateStatusBar();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.General,
                "Refresh cycle failed",
                detail: $"Attempted: full refresh (sync → scan → poll → verify → load). " +
                        $"Expected: completed. " +
                        $"Found: {ex.GetType().Name}: {ex.Message}. " +
                        $"Context: manual or timer refresh. " +
                        $"State: tree may show stale data.",
                ex: ex);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // TODO: update SettingsWindow to work without PrintStationDb
        // For now just open it with null db
        var settingsWindow = new SettingsWindow(_settings, _settingsManager, null);
        settingsWindow.Owner = this;
        if (settingsWindow.ShowDialog() == true)
        {
            ApplySettings();
            _refreshTimer.Stop();
            if (_settings.RefreshIntervalSeconds > 0)
            {
                _refreshTimer.Interval = TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds);
                _refreshTimer.Start();
            }
            _vm.LoadOrders();
        }
    }

    private bool IsPrintedTabActive => MainTabs.SelectedIndex == 1;

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs) return;
        ShowOrderDetail(null);
        DoneBtn.Content = IsPrintedTabActive ? "Mark Unprinted" : "Mark Printed";
    }

    private void ExpandAll_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        bool expand = ExpandAllCheck.IsChecked == true;
        foreach (var order in _vm.PendingOrders)
            order.IsExpanded = expand;
        foreach (var order in _vm.PrintedOrders)
            order.IsExpanded = expand;
        foreach (var order in _vm.OtherStoreOrders)
            order.IsExpanded = expand;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Action button handlers — delegate to ViewModel/services
    // ══════════════════════════════════════════════════════════════════════

    private void HoldButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedOrders();
        if (selected.Count == 0 && _selectedOrderItem != null)
            selected = new List<OrderTreeItem> { _selectedOrderItem };
        if (selected.Count == 0) return;

        foreach (var order in selected)
        {
            _vm.SelectedOrder = order;
            _vm.ToggleHold("operator");
            order.IsHeld = !order.IsHeld; // reflect immediately
        }
        _vm.LoadOrders();
        UpdateStatusBar();
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPrintedTabActive)
            MarkSelectedUnprinted();
        else
            OpenDoneConfirmForSelected(cursorOverEmail: false);
    }

    private void MarkSelectedUnprinted()
    {
        var selected = GetSelectedOrders();
        if (selected.Count == 0 && _selectedOrderItem != null)
            selected = new List<OrderTreeItem> { _selectedOrderItem };
        if (selected.Count == 0) return;

        var names = string.Join(", ", selected.Take(5).Select(o => o.ShortId));
        if (selected.Count > 5) names += $" +{selected.Count - 5} more";

        var result = MessageBox.Show(
            $"Mark {selected.Count} order(s) as UNPRINTED?\n\n{names}",
            "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        foreach (var order in selected)
        {
            _vm.MarkUnprinted(order.DbId);
            order.StatusCode = OrderStatusCode.New;
        }

        _vm.LoadOrders();
        UpdateStatusBar();
    }

    private void OpenDoneConfirmForSelected(bool cursorOverEmail)
    {
        var selected = GetSelectedOrders();
        if (selected.Count == 0 && _selectedOrderItem != null)
            selected = new List<OrderTreeItem> { _selectedOrderItem };
        if (selected.Count == 0) return;

        // For single order, show the confirm dialog with cursor positioning
        if (selected.Count == 1)
        {
            var order = selected[0];
            var win = new DoneConfirmWindow(
                order.CustomerName, order.ShortId,
                alreadyDone: order.StatusCode == OrderStatusCode.PickedUp,
                alreadyEmailed: false,
                cursorOverEmail: cursorOverEmail)
            { Owner = this };

            if (win.ShowDialog() == true)
                ApplyDoneAction(selected, win.Result);
        }
        else
        {
            // Batch: one confirm for all
            var names = string.Join(", ", selected.Take(5).Select(o => o.ShortId));
            if (selected.Count > 5) names += $" +{selected.Count - 5} more";

            var action = cursorOverEmail ? "mark printed + email" : "mark printed";
            var result = MessageBox.Show(
                $"{action.ToUpperInvariant()} {selected.Count} orders?\n\n{names}",
                "Batch Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
                ApplyDoneAction(selected, cursorOverEmail ? DoneAction.MarkDoneAndEmail : DoneAction.MarkDone);
        }

        _vm.LoadOrders();
        UpdateStatusBar();
    }

    private void ApplyDoneAction(List<OrderTreeItem> orders, DoneAction action)
    {
        foreach (var order in orders)
        {
            if (action == DoneAction.MarkDone || action == DoneAction.MarkDoneAndEmail)
            {
                _vm.MarkDone(order.DbId);
                order.StatusCode = OrderStatusCode.PickedUp;
            }
            if (action == DoneAction.MarkDoneAndEmail)
            {
                try
                {
                    _notificationService.NotifyCustomer(order.DbId, "operator");
                }
                catch (Exception ex)
                {
                    AlertCollector.Error(AlertCategory.Network,
                        $"Failed to notify customer for order {order.ShortId}",
                        orderId: order.ExternalOrderId,
                        detail: $"Attempted: send notification via NotificationService. " +
                                $"Expected: customer notified. " +
                                $"Found: {ex.GetType().Name}. " +
                                $"Context: ApplyDoneAction with MarkDoneAndEmail. " +
                                $"State: order {order.ExternalOrderId}, DbId={order.DbId}.",
                        ex: ex);
                }
            }
        }
    }

    private void NotifyButton_Click(object sender, RoutedEventArgs e)
    {
        OpenDoneConfirmForSelected(cursorOverEmail: true);
    }

    // ── Context menu handlers (right-click on order header) ──

    private OrderTreeItem? GetContextMenuOrder(object sender)
    {
        if (sender is MenuItem mi && mi.Parent is ContextMenu cm)
            return cm.PlacementTarget is FrameworkElement fe ? fe.DataContext as OrderTreeItem : null;
        return null;
    }

    private void ContextMenu_Email_Click(object sender, RoutedEventArgs e)
    {
        var order = GetContextMenuOrder(sender) ?? _selectedOrderItem;
        if (order == null) return;

        try
        {
            _notificationService.NotifyCustomer(order.DbId, "operator");
            _vm.LoadOrders();
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Network,
                $"Failed to notify customer for order {order.ShortId}",
                orderId: order.ExternalOrderId,
                detail: $"Attempted: send default email via NotificationService. " +
                        $"Expected: customer notified. " +
                        $"Found: {ex.GetType().Name}. " +
                        $"Context: right-click Email on order. " +
                        $"State: order {order.ExternalOrderId}, DbId={order.DbId}.",
                ex: ex);
        }
    }

    private void ContextMenu_OpenTemplate_Click(object sender, RoutedEventArgs e)
    {
        var order = GetContextMenuOrder(sender) ?? _selectedOrderItem;
        if (order == null) return;

        if (_settings.EmailTemplates.Count == 0)
        {
            AlertCollector.Error(AlertCategory.Settings,
                "No email templates configured. Open Settings > Notifications to create templates.");
            return;
        }

        // Step 1: Choose template
        var fullOrder = _orders.GetFullOrder(order.DbId);
        if (fullOrder == null)
        {
            AlertCollector.Error(AlertCategory.Database,
                $"Cannot load order {order.ShortId} for email preview",
                orderId: order.ExternalOrderId);
            return;
        }

        var isShipped = fullOrder.DeliveryMethodId == DeliveryMethodId.Ship;
        var defaultName = _settings.GetDefaultTemplate(isShipped)?.Name;
        var chooser = new EmailTemplateChooser(_settings.EmailTemplates, defaultName) { Owner = this };
        if (chooser.ShowDialog() != true || chooser.SelectedTemplate == null) return;

        // Step 2: Preview with placeholders filled
        var template = chooser.SelectedTemplate;
        var subject = Core.Processing.EmailService.ReplacePlaceholders(template.Subject, fullOrder);
        var body = Core.Processing.EmailService.ReplacePlaceholders(template.Body, fullOrder);

        var preview = new SendEmailWindow(fullOrder.CustomerEmail ?? "", subject, body) { Owner = this };
        if (preview.ShowDialog() != true) return;

        // Step 3: Send through NotificationService
        try
        {
            _notificationService.NotifyCustomer(order.DbId, "operator", template);
            _vm.LoadOrders();
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Network,
                $"Failed to notify customer for order {order.ShortId}",
                orderId: order.ExternalOrderId,
                detail: $"Attempted: send email with template '{template.Name}'. " +
                        $"Expected: customer notified. " +
                        $"Found: {ex.GetType().Name}. " +
                        $"Context: right-click Open Email Template. " +
                        $"State: order {order.ExternalOrderId}, DbId={order.DbId}.",
                ex: ex);
        }
    }

    private void ContextMenu_MarkDone_Click(object sender, RoutedEventArgs e)
    {
        if (IsPrintedTabActive)
        {
            MarkSelectedUnprinted();
        }
        else
        {
            OpenDoneConfirmForSelected(cursorOverEmail: false);
        }
    }

    private void TransferButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem == null) return;

        var transfer = App.Services.GetService<Core.Services.ITransferService>();
        if (transfer == null)
        {
            AlertCollector.Error(AlertCategory.Transfer,
                "TransferService not registered",
                detail: "Attempted: open transfer window. Expected: ITransferService in DI. Found: null.");
            return;
        }

        var win = new TransferWindow(
            _selectedOrderItem.DbId,
            _selectedOrderItem.ExternalOrderId,
            _selectedOrderItem.FolderPath,
            transfer,
            _orders,
            _settings)
        { Owner = this };

        if (win.ShowDialog() == true)
        {
            _vm.LoadOrders();
            UpdateStatusBar();
        }
    }

    private async void PrintSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var orders = GetSelectedOrders();
        if (orders.Count == 0 && _selectedOrderItem != null)
            orders = new List<OrderTreeItem> { _selectedOrderItem };
        if (orders.Count == 0) return;

        // Size filter only applies when printing a single order with specific sizes selected
        var selectedSizes = GetSelectedSizes();
        var sizeFilter = orders.Count == 1 && selectedSizes.Count > 0
            ? selectedSizes.Select(s => $"{s.SizeLabel}|{s.MediaType}").ToHashSet()
            : null;

        PrintBtn.IsEnabled = false;
        PrintBtn.Content = "Printing...";

        int totalSent = 0;
        int totalSkipped = 0;
        var allSkipReasons = new List<string>();

        try
        {
            foreach (var order in orders)
            {
                // Transfer mismatch check — warn if files don't match DB after transfer
                if (order.IsTransfer)
                {
                    var mismatches = _vm.CheckTransferMismatches(order.DbId);
                    if (mismatches.Count > 0)
                    {
                        var details = string.Join("\n", mismatches.Select(m => $"  {m.ItemDescription}: {m.Issue}"));
                        var proceed = MessageBox.Show(
                            $"Order {order.ShortId} was transferred and has {mismatches.Count} mismatch(es):\n\n{details}\n\nPrint anyway?",
                            "Transfer Mismatch", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (proceed != MessageBoxResult.Yes)
                            continue;
                    }
                }

                var result = await Task.Run(() => _vm.PrintOrder(
                    order.DbId, order.ExternalOrderId, order.FolderPath, order.SourceCode, sizeFilter));

                totalSent += result.Sent.Count;
                totalSkipped += result.Skipped.Count;

                foreach (var s in result.Skipped)
                    allSkipReasons.Add($"{order.ShortId} — {s.SizeLabel}: {s.Reason}");
            }

            if (totalSent > 0)
                _vm.StatusText = $"Sent {totalSent} item(s) across {orders.Count} order(s)";

            if (totalSkipped > 0)
            {
                MessageBox.Show($"Skipped {totalSkipped} size group(s):\n\n{string.Join("\n", allSkipReasons)}",
                    "Print — Some Sizes Skipped", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _vm.LoadOrders();
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            AlertCollector.Error(Core.AlertCategory.Printing,
                "Batch print failed", ex: ex);
            MessageBox.Show($"Print failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            PrintBtn.IsEnabled = true;
            PrintBtn.Content = "Print Selected";
        }
    }

    private void ChangeSizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem == null || _selectedSizeItem == null) return;

        var order = _selectedOrderItem.Order;
        if (order == null)
        {
            MessageBox.Show("Order data not loaded.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var channels = _vm.GetAllChannels();
        var items = _selectedSizeItem.Items;

        var win = new ChangeSizeWindow(
            _selectedSizeItem.SizeLabel,
            order,
            items,
            channels,
            _settings,
            _orders);
        win.Owner = this;
        win.ShowDialog();

        // Refresh tree after changes
        _vm.LoadOrders();
        UpdateStatusBar();
    }

    private void ColorCorrectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem == null) return;

        var order = _selectedOrderItem.Order;
        if (order == null)
        {
            MessageBox.Show("Order data not loaded.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Build list of sizes to process: single size if selected, all sizes if order selected
        var sizesToProcess = _selectedSizeItem != null
            ? new List<SizeTreeItem> { _selectedSizeItem }
            : _selectedOrderItem.Sizes.ToList();

        if (sizesToProcess.Count == 0)
        {
            MessageBox.Show("No sizes found.", "Nothing to Correct", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var completedSizes = new List<SizeTreeItem>();

        foreach (var sz in sizesToProcess)
        {
            var imagePaths = sz.Items
                .Where(i => !string.IsNullOrEmpty(i.ImageFilepath) && File.Exists(i.ImageFilepath))
                .Select(i => i.ImageFilepath!)
                .ToList();

            if (imagePaths.Count == 0) continue;

            var vm = new ColorCorrectWindowViewModel(
                order.ExternalOrderId,
                order.FolderPath ?? "",
                sz.SizeLabel,
                imagePaths,
                _correctionStore,
                _settings,
                processed => { /* corrections saved to CorrectionStore by the VM */ });

            var win = new ColorCorrectWindow
            {
                DataContext = vm,
                Owner = this,
                Title = $"Color Correction — {order.CustomerFirstName} {order.CustomerLastName} — {sz.DisplayLabel} ({sizesToProcess.IndexOf(sz) + 1}/{sizesToProcess.Count})"
            };

            StopTimers();
            try { win.ShowDialog(); }
            finally { StartTimers(); }

            if (vm.IsComplete)
                completedSizes.Add(sz);
            else
                break; // User cancelled — stop processing remaining sizes
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem == null) return;
        var folder = _selectedOrderItem.FolderPath;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem == null) return;
        var label = $"{_selectedOrderItem.CustomerName} — {_selectedOrderItem.ExternalOrderId}";
        var notes = _vm.OrderNotes.ToList();
        var win = new OrderHistoryWindow(label, notes) { Owner = this };
        win.ShowDialog();
    }

    private static string FormatPhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 10)
            return $"({digits[..3]}) {digits[3..6]}-{digits[6..]}";
        if (digits.Length == 11 && digits[0] == '1')
            return $"({digits[1..4]}) {digits[4..7]}-{digits[7..]}";
        return raw;
    }

    // ── Timer helpers ──────────────────────────────────────────────────────

    private void StopTimers()
    {
        _refreshTimer.Stop();
        _verifyTimer.Stop();
    }

    private void StartTimers()
    {
        if (_settings.RefreshIntervalSeconds > 0)
            _refreshTimer.Start();
        _verifyTimer.Start();
    }
}

// Value converters (used by XAML bindings)
public class BoolToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

public class StringToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public static readonly StringToVisibilityConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
