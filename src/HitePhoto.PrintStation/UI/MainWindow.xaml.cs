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
using HitePhoto.PrintStation.UI.ViewModels;

namespace HitePhoto.PrintStation.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly SettingsManager _settingsManager;

    // Timers
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _alertDrainTimer;
    private readonly DispatcherTimer _pixfizzPollTimer;
    private readonly DispatcherTimer _dakisScanTimer;

    // Cancellation for async ingest
    private CancellationTokenSource _ingestCts = new();

    // Currently selected (for detail panel)
    private OrderTreeItem? _selectedOrderItem;
    private SizeTreeItem? _selectedSizeItem;

    public MainWindow(MainViewModel vm, AppSettings settings, SettingsManager settingsManager)
    {
        InitializeComponent();

        _vm = vm;
        _settings = settings;
        _settingsManager = settingsManager;

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
        var alerts = AlertCollector.GetAndClear();
        if (alerts.Count == 0) return;

        var errors = alerts.Where(a => a.Severity == AlertSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            var text = string.Join("\n\n", errors.Select(a => a.TechnicalDump()));
            MessageBox.Show(text, $"Errors ({errors.Count})", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            _selectedOrderItem = orderItem;
            _vm.SelectedOrder = orderItem;
            ShowOrderDetail(orderItem);
            ShowSizeDetail(null);
        }
        else if (selectedItem is SizeTreeItem sizeItem)
        {
            _selectedSizeItem = sizeItem;
            ShowSizeDetail(sizeItem);
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
        _selectedSizeItem = sizeItem;

        if (sizeItem == null)
        {
            SizeDetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        SizeDetailPanel.Visibility = Visibility.Visible;
        SizeDetailLabel.Text = sizeItem.DisplayLabel;
        SizeDetailChannel.Text = sizeItem.ChannelLabel;
    }

    private void LoadThumbnails(OrderTreeItem treeItem)
    {
        ThumbnailPanel.Children.Clear();

        var allItems = treeItem.Sizes.SelectMany(s => s.Items).ToList();
        if (allItems.Count == 0) return;

        foreach (var item in allItems.Take(30))
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

    private void PrintSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem == null) return;
        // TODO: delegate to PrintService via ViewModel
        MessageBox.Show("Print not yet wired.", "TODO", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ColorCorrectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem == null || _selectedSizeItem == null) return;
        // TODO: open color correct window with ViewModel data
        MessageBox.Show("Color correct not yet wired.", "TODO", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem == null) return;
        var folder = _selectedOrderItem.FolderPath;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
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
