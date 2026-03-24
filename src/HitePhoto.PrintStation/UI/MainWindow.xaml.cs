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

        // Refresh timer — reload from SQLite periodically
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds) };
        _refreshTimer.Tick += (_, _) => _vm.LoadOrders();

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

        // Dakis scan timer
        _dakisScanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _dakisScanTimer.Tick += (_, _) =>
        {
            try { _vm.RunDakisScan(); }
            catch (Exception ex) { AlertCollector.Error(AlertCategory.Parsing, "Dakis scan failed", ex: ex); }
        };

        Loaded += MainWindow_Loaded;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Startup
    // ══════════════════════════════════════════════════════════════════════

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySettings();

        // Load orders from SQLite
        _vm.LoadOrders();
        UpdateStatusBar();

        // Start timers
        _refreshTimer.Start();
        if (_settings.PixfizzEnabled) _pixfizzPollTimer.Start();
        if (_settings.DakisEnabled) _dakisScanTimer.Start();
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
        if (alerts.Count == 0)
        {
            AlertCountText.Text = "";
            return;
        }

        AlertCountText.Text = $"Alerts: {alerts.Count}";
        AlertCountText.Visibility = Visibility.Visible;

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
        if (SourceFilterCombo.SelectedItem is ComboBoxItem item)
        {
            _vm.SourceFilter = item.Content?.ToString() ?? "All";
            _vm.LoadOrders();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _vm.SearchText = SearchBox.Text;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
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

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem == null) return;
        // TODO: get folder path from ViewModel/order data
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

    private void ChangeSizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderItem == null || _selectedSizeItem == null) return;
        // TODO: open change size window
        MessageBox.Show("Change size not yet wired.", "TODO", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void AssignChannelButton_Click(object sender, RoutedEventArgs e)
    {
        // TODO: delegate channel assignment to ViewModel
    }

    private void AlertCountText_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var alerts = AlertCollector.GetAndClear();
        if (alerts.Count == 0) return;
        var text = string.Join("\n\n", alerts.Select(a => a.TechnicalDump()));
        MessageBox.Show(text, $"Alerts ({alerts.Count})", MessageBoxButton.OK, MessageBoxImage.Information);
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
