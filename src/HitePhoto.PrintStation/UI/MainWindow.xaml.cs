using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Core.Models;
using HitePhoto.PrintStation.Core.Processing;
using HitePhoto.PrintStation.Data;
using HitePhoto.PrintStation.Data.Repositories;
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

    private readonly System.Collections.ObjectModel.ObservableCollection<AlertItemViewModel> _sessionAlerts = new();
    private int _nextAlertId;

    // Timers
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _alertDrainTimer;
    private readonly DispatcherTimer _pixfizzPollTimer;
    private readonly DispatcherTimer _dakisScanTimer;

    // Cancellation for async ingest
    private CancellationTokenSource _ingestCts = new();

    // Click-verify: debounce + background thread
    private readonly DispatcherTimer _verifyDebounce;
    private OrderTreeItem? _pendingVerifyOrder;
    private CancellationTokenSource _verifyCts = new();

    // Currently selected (for detail panel)
    private OrderTreeItem? _selectedOrderItem;
    private SizeTreeItem? _selectedSizeItem;

    public MainWindow(MainViewModel vm, AppSettings settings, SettingsManager settingsManager,
        Data.Repositories.IOrderRepository orders, Data.CorrectionStore correctionStore,
        IAlertRepository alertRepo)
    {
        InitializeComponent();

        Title = $"HitePhoto Print Station — build {BuildInfo.BuildTimestamp}";

        _vm = vm;
        _settings = settings;
        _settingsManager = settingsManager;
        _orders = orders;
        _correctionStore = correctionStore;
        _alertRepo = alertRepo;

        PendingTree.ItemsSource = _vm.PendingOrders;
        PrintedTree.ItemsSource = _vm.PrintedOrders;
        OtherStoreTree.ItemsSource = _vm.OtherStoreOrders;

        // Refresh timer — only reload if data changed
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds) };
        _refreshTimer.Tick += (_, _) =>
        {
            if (_vm.NeedsRefresh)
            {
                _vm.LoadOrders();
                UpdateStatusBar();
                _vm.NeedsRefresh = false;
            }
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
        };

        // Alert drain
        _alertDrainTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _alertDrainTimer.Tick += (_, _) => DrainAlerts();
        _alertDrainTimer.Start();

        AlertList.ItemsSource = _sessionAlerts;

        // Pixfizz poll timer
        _pixfizzPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.PollIntervalSeconds) };
        _pixfizzPollTimer.Tick += async (_, _) =>
        {
            try { await _vm.RunPixfizzPollAsync(_ingestCts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AlertCollector.Error(AlertCategory.Network, "Pixfizz poll failed", ex: ex); }
        };

        // Dakis scan timer — fallback in case FileSystemWatcher misses an event
        _dakisScanTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _dakisScanTimer.Tick += (_, _) =>
        {
            Task.Run(() => _vm.RunDakisScan());
        };

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

        // Step 7: Start timers
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
                if (_settings.PixfizzEnabled && !string.IsNullOrWhiteSpace(_settings.PixfizzApiKey))
                    _pixfizzPollTimer.Start();

                if (_settings.DakisEnabled && !string.IsNullOrWhiteSpace(_settings.DakisWatchFolder))
                {
                    _vm.StartDakisWatcher();
                    _dakisScanTimer.Start(); // fallback scan
                }
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
        Title = _settings.DeveloperMode ? "HitePhoto Print Station [DEV MODE]" : "HitePhoto Print Station";
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

    private void PendingTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => HandleTreeSelection(e.NewValue);

    private void PrintedTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => HandleTreeSelection(e.NewValue);

    private void OtherStoreTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => HandleTreeSelection(e.NewValue);

    private void HandleTreeSelection(object? selectedItem)
    {
        if (selectedItem is OrderTreeItem orderItem)
        {
            var isNewSelection = _selectedOrderItem?.DbId != orderItem.DbId;
            _selectedOrderItem = orderItem;
            _vm.SelectedOrder = orderItem;

            if (isNewSelection)
            {
                _pendingVerifyOrder = orderItem;
                _verifyDebounce.Stop();
                _verifyDebounce.Start();
            }

            ShowOrderDetail(orderItem);
            ShowSizeDetail(null);
        }
        else if (selectedItem is SizeTreeItem sizeItem)
        {
            _selectedSizeItem = sizeItem;

            if (sizeItem.ParentOrder != null && _selectedOrderItem?.DbId != sizeItem.ParentOrder.DbId)
            {
                _selectedOrderItem = sizeItem.ParentOrder;
                _vm.SelectedOrder = sizeItem.ParentOrder;
                _pendingVerifyOrder = sizeItem.ParentOrder;
                _verifyDebounce.Stop();
                _verifyDebounce.Start();
                ShowOrderDetail(sizeItem.ParentOrder);
            }

            ShowSizeDetail(sizeItem);
            LoadSizeThumbnails(sizeItem);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Detail panel
    // ══════════════════════════════════════════════════════════════════════

    private void ShowOrderDetail(OrderTreeItem? treeItem)
    {
        if (treeItem == null)
        {
            DetailEmpty.Visibility = Visibility.Visible;
            DetailContent.Visibility = Visibility.Collapsed;
            return;
        }

        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailContent.Visibility = Visibility.Visible;

        DetailOrderId.Text = treeItem.ExternalOrderId;
        DetailStatus.Text = treeItem.StatusCode;
        DetailSource.Text = treeItem.SourceCode;
        DetailStore.Text = treeItem.StoreName;
        DetailCustomerName.Text = treeItem.CustomerName;
        DetailOrderedAt.Text = treeItem.OrderedAt?.ToString("yyyy-MM-dd HH:mm") ?? "";
        DetailItemCount.Text = $"{treeItem.TotalImages}";

        // Notes from ViewModel
        NotesListBox.ItemsSource = _vm.OrderNotes;

        SizeDetailPanel.Visibility = Visibility.Collapsed;

        LoadThumbnails(treeItem);
    }

    private void ShowSizeDetail(SizeTreeItem? sizeItem)
    {
        if (sizeItem == null)
        {
            SizeDetailPanel.Visibility = Visibility.Collapsed;
            ChannelList.Visibility = Visibility.Collapsed;
            return;
        }

        SizeDetailPanel.Visibility = Visibility.Visible;
        SizeDetailLabel.Text = sizeItem.DisplayLabel;
        SizeDetailChannel.Text = sizeItem.ChannelLabel;
        ChannelSearchBox.Text = "";
        ChannelList.Visibility = Visibility.Collapsed;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Channel assignment
    // ══════════════════════════════════════════════════════════════════════

    private List<(string Display, int Channel)> _channelEntries = new();
    private List<(string Display, int Channel)> _filteredChannelEntries = new();
    private bool _channelEntriesLoaded;

    private void LoadChannelEntries()
    {
        _channelEntries.Clear();

        // Layouts first
        foreach (var l in _settings.Layouts)
        {
            _channelEntries.Add((
                $"[Layout] {l.Name}  ({l.PrintSizeDisplay} {l.GridDisplay} → CH {l.TargetChannelNumber:D3})",
                l.TargetChannelNumber));
        }

        // Channels from Noritsu CSV
        if (!string.IsNullOrWhiteSpace(_settings.ChannelsCsvPath))
        {
            var reader = new Core.Processing.ChannelsCsvReader(_settings.ChannelsCsvPath);
            var csvChannels = reader.Load();
            foreach (var c in csvChannels)
            {
                _channelEntries.Add((
                    $"{c.ChannelNumber:D3}  {c.SizeLabel}  {c.MediaType}  {c.Description}".Trim(),
                    c.ChannelNumber));
            }
        }

        // Existing mappings from DB (in case CSV doesn't cover everything)
        var dbChannels = _vm.GetAllChannels();
        var existingNumbers = new HashSet<int>(_channelEntries.Select(e => e.Channel));
        foreach (var c in dbChannels)
        {
            if (!existingNumbers.Contains(c.ChannelNumber))
            {
                _channelEntries.Add((
                    $"{c.ChannelNumber:D3}  {c.SizeLabel}  {c.MediaType}  {c.Description}".Trim(),
                    c.ChannelNumber));
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
        ChannelList.Visibility = Visibility.Visible;
    }

    private void ChannelSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (!_channelEntriesLoaded) LoadChannelEntries();
        PopulateChannelList();
        ChannelList.Visibility = Visibility.Visible;
    }

    private void ChannelSearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!ChannelList.IsKeyboardFocusWithin)
            ChannelList.Visibility = Visibility.Collapsed;
    }

    private void AssignChannel_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSizeItem == null || ChannelList.SelectedIndex < 0) return;

        var selected = _filteredChannelEntries[ChannelList.SelectedIndex];
        _vm.AssignChannel(_selectedSizeItem.SizeLabel, _selectedSizeItem.MediaType, selected.Channel);

        // Update the tree immediately
        _selectedSizeItem.ChannelNumber = selected.Channel;
        SizeDetailChannel.Text = _selectedSizeItem.ChannelLabel;
        ChannelList.Visibility = Visibility.Collapsed;
        ChannelSearchBox.Text = "";

        _channelEntriesLoaded = false; // invalidate cache
        _vm.LoadOrders();
        UpdateStatusBar();
    }

    private void LoadThumbnails(OrderTreeItem treeItem)
        => LoadThumbnailsFromItems(treeItem.Sizes.SelectMany(s => s.Items));

    private void LoadSizeThumbnails(SizeTreeItem sizeItem)
        => LoadThumbnailsFromItems(sizeItem.Items);

    private void LoadThumbnailsFromItems(IEnumerable<HitePhoto.Shared.Models.OrderItem> items)
    {
        ThumbnailPanel.Children.Clear();

        foreach (var item in items.Take(30))
        {
            if (string.IsNullOrEmpty(item.ImageFilepath)) continue;

            var border = new Border
            {
                Width = 80, Height = 80,
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
                    border.Child = new Image { Source = bmp, Stretch = Stretch.Uniform };
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
                    Text = "Missing", FontSize = 9,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("AccentRed")
                };
            }

            ThumbnailPanel.Children.Add(border);
        }
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

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _vm.LoadOrders();
        UpdateStatusBar();
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
            _refreshTimer.Interval = TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds);
            _pixfizzPollTimer.Interval = TimeSpan.FromSeconds(_settings.PollIntervalSeconds);
            _vm.LoadOrders();
        }
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs) return;
        ShowOrderDetail(null);
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
        if (_selectedOrderItem == null) return;
        _vm.ToggleHold("operator");
        _vm.LoadOrders();
        UpdateStatusBar();
    }

    private void NotifyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem == null) return;
        // TODO: delegate to NotificationService via ViewModel
        MessageBox.Show("Notification not yet wired.", "TODO", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void TransferButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem == null) return;
        // TODO: delegate to TransferService via ViewModel
        MessageBox.Show("Transfer not yet wired.", "TODO", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void PrintSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem == null) return;

        if (_settings.DeveloperMode)
        {
            MessageBox.Show("Printing is disabled in developer mode.", "Dev Mode",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var order = _selectedOrderItem;
        PrintBtn.IsEnabled = false;
        PrintBtn.Content = "Printing...";

        try
        {
            var result = await Task.Run(() => _vm.PrintOrder(
                order.DbId, order.ExternalOrderId, order.FolderPath, order.SourceCode));

            var sentCount = result.Sent.Count;
            var skippedCount = result.Skipped.Count;

            if (sentCount > 0)
            {
                var channels = string.Join(", ", result.Sent.Select(s => $"Ch {s.ChannelNumber:D3}").Distinct());
                _vm.StatusText = $"Sent {sentCount} item(s) to {channels}";
            }

            if (skippedCount > 0)
            {
                var reasons = string.Join("\n", result.Skipped.Select(s => $"{s.SizeLabel}: {s.Reason}"));
                MessageBox.Show($"Skipped {skippedCount} size group(s):\n\n{reasons}",
                    "Print — Some Sizes Skipped", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _vm.LoadOrders();
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            AlertCollector.Error(Core.AlertCategory.Printing,
                $"Print failed for {order.ExternalOrderId}", ex: ex);
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

    // ── Timer helpers ──────────────────────────────────────────────────────

    private void StopTimers()
    {
        _refreshTimer.Stop();
        _pixfizzPollTimer.Stop();
        _dakisScanTimer.Stop();
    }

    private void StartTimers()
    {
        _refreshTimer.Start();
        _pixfizzPollTimer.Start();
        _dakisScanTimer.Start();
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
