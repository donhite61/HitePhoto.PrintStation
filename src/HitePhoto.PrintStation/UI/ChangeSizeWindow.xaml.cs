using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Core.Models;
using HitePhoto.PrintStation.Core.Processing;
using HitePhoto.PrintStation.Data.Repositories;
using HitePhoto.Shared.Models;

namespace HitePhoto.PrintStation.UI;

public partial class ChangeSizeWindow : Window
{
    private readonly string _sizeLabel;
    private readonly Order _order;
    private readonly List<OrderItem> _items;
    private readonly List<ChannelInfo> _allChannels;
    private readonly AppSettings _settings;
    private readonly IOrderRepository _orders;

    private readonly List<ImageCard> _cards = new();

    public ChangeSizeWindow(
        string sizeLabel,
        Order order,
        List<OrderItem> items,
        List<ChannelInfo> allChannels,
        AppSettings settings,
        IOrderRepository orders)
    {
        InitializeComponent();
        _sizeLabel = sizeLabel;
        _order = order;
        _items = items;
        _allChannels = allChannels;
        _settings = settings;
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));

        Title = $"Preview — Order {order.ExternalOrderId}  ({order.CustomerFirstName} {order.CustomerLastName})";
        SizeTitleText.Text = sizeLabel;

        PopulateChannelCombo();
        BuildImageGrid();
        UpdateSelectionStatus();
    }

    // ── Channel combo ─────────────────────────────────────────────────────

    private void PopulateChannelCombo()
    {
        var comboItems = new List<SendToItem>();

        // Layouts first
        var layouts = _settings.Layouts ?? new();
        foreach (var l in layouts)
        {
            comboItems.Add(new SendToItem
            {
                DisplayName = $"[Layout]  {l.Name}  ({l.PrintSizeDisplay} {l.GridDisplay} → CH {l.TargetChannelNumber:D3} {l.TargetSizeLabel})",
                Layout = l
            });
        }

        if (layouts.Count > 0 && _allChannels.Count > 0)
            comboItems.Add(new SendToItem { DisplayName = "───────────────────────────", IsSeparator = true });

        // Skip option
        comboItems.Insert(0, new SendToItem
        {
            DisplayName = "— Skip (no print) —",
            ChannelNumber = -1
        });

        // All channels
        foreach (var c in _allChannels)
        {
            comboItems.Add(new SendToItem
            {
                DisplayName = string.Join("  ", new[] { c.ChannelNumber.ToString("D3"), c.SizeLabel, c.MediaType, c.Description }.Where(p => !string.IsNullOrWhiteSpace(p))),
                ChannelNumber = c.ChannelNumber,
                ChannelInfo = c
            });
        }

        ChannelCombo.ItemsSource = comboItems;

        // Pre-select current channel
        int currentChannel = _items.FirstOrDefault()?.ChannelNumber ?? 0;
        int preselect = -1;

        // Check layout assignment
        var routingKey = $"size={_sizeLabel}|media={_items.FirstOrDefault()?.MediaType ?? ""}".ToLowerInvariant();
        if (_settings.RoutingMap.TryGetValue(routingKey, out var entry) && !string.IsNullOrEmpty(entry?.LayoutName))
            preselect = comboItems.FindIndex(i => i.Layout != null &&
                i.Layout.Name.Equals(entry.LayoutName, StringComparison.OrdinalIgnoreCase));

        if (preselect < 0 && currentChannel == -1)
            preselect = comboItems.FindIndex(i => i.ChannelNumber == -1 && i.ChannelInfo == null && i.Layout == null);

        if (preselect < 0 && currentChannel > 0)
            preselect = comboItems.FindIndex(i => i.ChannelInfo != null && i.ChannelInfo.ChannelNumber == currentChannel);

        if (preselect >= 0)
            ChannelCombo.SelectedIndex = preselect;
        else if (comboItems.Count > 0)
            ChannelCombo.SelectedIndex = 0;
    }

    // ── Image grid ───────────────────────────────────────────────────────

    private void BuildImageGrid()
    {
        ImageGrid.Children.Clear();
        _cards.Clear();

        foreach (var item in _items)
        {
            var card = new ImageCard(item);
            card.SelectionChanged += UpdateSelectionStatus;
            _cards.Add(card);
            ImageGrid.Children.Add(card.Visual);
        }
    }

    // ── Check all / none ─────────────────────────────────────────────────

    private bool _suppressCheckAll;

    private void CheckAllBox_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressCheckAll) return;
        foreach (var c in _cards) c.IsSelected = true;
        UpdateSelectionStatus();
    }

    private void CheckAllBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressCheckAll) return;
        foreach (var c in _cards) c.IsSelected = false;
        UpdateSelectionStatus();
    }

    private void UpdateSelectionStatus(object? sender = null, EventArgs? e = null)
    {
        if (SelectionStatus == null || CheckAllBox == null) return;
        int selected = _cards.Count(c => c.IsSelected);
        int total = _cards.Count;
        SelectionStatus.Text = $"{selected} of {total} selected";

        _suppressCheckAll = true;
        CheckAllBox.IsChecked = selected == total ? true :
                                selected == 0 ? false : null;
        _suppressCheckAll = false;
    }

    // ── Print ─────────────────────────────────────────────────────────────

    private void PrintBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.DeveloperMode)
        {
            MessageBox.Show("Printing is disabled in Developer Mode.", "Developer Mode",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ChannelCombo.SelectedItem is not SendToItem item || item.IsSeparator)
        {
            MessageBox.Show("Select a channel or layout first.", "No Destination",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var toPrint = _cards.Where(c => c.IsSelected && !string.IsNullOrEmpty(c.Item.ImageFilepath) && File.Exists(c.Item.ImageFilepath)).ToList();
        if (toPrint.Count == 0)
        {
            MessageBox.Show("No images selected (or all selected images are missing).",
                "Nothing to Print", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var writer = new NoritsuMrkWriter(_settings.NoritsuOutputRoot);
            int printCount;
            int chUsed;
            string statusMsg;

            if (item.Layout != null)
            {
                var layout = item.Layout;
                var processedImages = new List<(string LayoutImagePath, string OriginalName, int Quantity)>();

                foreach (var card in toPrint)
                {
                    string sourcePath = card.Item.ImageFilepath!;
                    string outputPath = LayoutProcessor.BuildLayoutPath(
                        sourcePath, _order.FolderPath ?? Path.GetDirectoryName(sourcePath)!, layout.Name);

                    LayoutProcessor.ApplyLayout(sourcePath, outputPath, layout);
                    processedImages.Add((outputPath, card.Item.ImageFilename ?? Path.GetFileName(sourcePath), card.Quantity));
                }

                // Build items list for the MRK writer
                var layoutItems = toPrint.Select(c => new OrderItem
                {
                    Id = c.Item.Id,
                    ImageFilepath = LayoutProcessor.BuildLayoutPath(
                        c.Item.ImageFilepath!, _order.FolderPath ?? Path.GetDirectoryName(c.Item.ImageFilepath!)!, layout.Name),
                    ImageFilename = c.Item.ImageFilename,
                    Quantity = c.Quantity
                }).ToList();

                writer.WriteMrk(_order, layout.TargetSizeLabel, layout.TargetChannelNumber, layoutItems);
                printCount = processedImages.Sum(p => p.Quantity);
                chUsed = layout.TargetChannelNumber;
                statusMsg = $"Sent {printCount} print(s) to CH {chUsed:D3} via '{layout.Name}'";
            }
            else
            {
                // Standard flow — direct channel
                var printItems = toPrint.Select(c =>
                {
                    var clone = new OrderItem
                    {
                        Id = c.Item.Id,
                        OrderId = c.Item.OrderId,
                        SizeLabel = c.Item.SizeLabel,
                        MediaType = c.Item.MediaType,
                        ImageFilepath = c.Item.ImageFilepath,
                        ImageFilename = c.Item.ImageFilename,
                        Quantity = c.Quantity,
                        ChannelNumber = item.ChannelNumber
                    };
                    return clone;
                }).ToList();

                writer.WriteMrk(_order, _sizeLabel, item.ChannelNumber, printItems);
                printCount = toPrint.Sum(c => c.Quantity);
                chUsed = item.ChannelNumber;
                statusMsg = $"Sent {printCount} print(s) to CH {chUsed:D3}";
            }

            // Mark items as printed in DB
            var printedIds = toPrint.Select(c => c.Item.Id).ToList();
            _orders.SetItemsPrinted(printedIds);
            var now = DateTime.Now;
            foreach (var card in toPrint)
            {
                card.Item.IsPrinted = true;
                card.Item.PrintedAt = now;
            }

            if (CloseWhenPrintedBox.IsChecked == true)
            {
                PrintBtn.IsEnabled = false;
                PrintBtn.Content = statusMsg;
                var timer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(1500) };
                timer.Tick += (_, _) => { timer.Stop(); Close(); };
                timer.Start();
            }
            else
            {
                PrintBtn.Content = statusMsg;
                PrintBtn.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Printing,
                "Print from ChangeSizeWindow failed",
                orderId: _order.ExternalOrderId,
                detail: $"Attempted: print {toPrint.Count} images for size '{_sizeLabel}'. " +
                        $"Expected: MRK written to '{_settings.NoritsuOutputRoot}'. Found: exception. " +
                        $"State: order {_order.ExternalOrderId}, channel {(ChannelCombo.SelectedItem as SendToItem)?.ChannelNumber}.",
                ex: ex);
            MessageBox.Show($"Print error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}

// ── Send-to item (combined channel + layout dropdown) ─────────────────────

internal class SendToItem
{
    public string DisplayName { get; set; } = "";
    public int ChannelNumber { get; set; }
    public ChannelInfo? ChannelInfo { get; set; }
    public LayoutDefinition? Layout { get; set; }
    public bool IsSeparator { get; set; }

    public override string ToString() => DisplayName;
}

// ── Image card (built in code, displays thumbnail + checkbox + qty) ──────

internal class ImageCard
{
    public OrderItem Item { get; }
    public UIElement Visual { get; }
    public event EventHandler? SelectionChanged;

    private readonly CheckBox _checkBox;
    private readonly TextBlock _qtyDisplay;
    private readonly TextBox _qtyEdit;
    private int _qty;

    public bool IsSelected
    {
        get => _checkBox.IsChecked == true;
        set => _checkBox.IsChecked = value;
    }

    public int Quantity
    {
        get => _qty;
        set
        {
            _qty = Math.Max(1, Math.Min(999, value));
            _qtyDisplay.Text = _qty.ToString();
            _qtyEdit.Text = _qty.ToString();
        }
    }

    public ImageCard(OrderItem item)
    {
        Item = item;

        var border = new Border
        {
            Width = 190,
            Margin = new Thickness(6),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.FindResource("ButtonBorder"),
            Background = (Brush)Application.Current.FindResource("SurfaceBg"),
            CornerRadius = new CornerRadius(4)
        };

        var stack = new StackPanel { Margin = new Thickness(8) };

        // Filename
        string fileName = item.ImageFilename ?? Path.GetFileName(item.ImageFilepath ?? "unknown");
        stack.Children.Add(new TextBlock
        {
            Text = fileName,
            Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 4),
            ToolTip = item.ImageFilepath
        });

        // Thumbnail
        bool exists = !string.IsNullOrEmpty(item.ImageFilepath) && File.Exists(item.ImageFilepath);
        if (exists)
        {
            var imgCtrl = new System.Windows.Controls.Image
            {
                Height = 130,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 0, 6)
            };

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 190;
                bmp.UriSource = new Uri(item.ImageFilepath!);
                bmp.EndInit();
                bmp.Freeze();
                imgCtrl.Source = bmp;
            }
            catch
            {
                AlertCollector.Warn(AlertCategory.DataQuality,
                    "Could not load thumbnail",
                    detail: $"Attempted: load thumbnail for '{item.ImageFilepath}'. Found: decode failed.");
                imgCtrl.Source = null;
            }

            stack.Children.Add(imgCtrl);
        }
        else
        {
            stack.Children.Add(new TextBlock
            {
                Text = "File missing",
                Foreground = new SolidColorBrush(Colors.OrangeRed),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Height = 130
            });
        }

        // Bottom row: checkbox + qty
        var bottomRow = new StackPanel { Orientation = Orientation.Horizontal };

        _checkBox = new CheckBox
        {
            Content = "Print",
            IsChecked = exists,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        _checkBox.Checked += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        _checkBox.Unchecked += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);

        _qty = item.Quantity;

        _qtyDisplay = new TextBlock
        {
            Text = _qty.ToString(),
            Width = 36,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
            Cursor = Cursors.Hand,
            ToolTip = "Left-click -1 · Right-click +1 · Scroll wheel"
        };

        _qtyEdit = new TextBox
        {
            Text = _qty.ToString(),
            Width = 36,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Visibility = Visibility.Collapsed
        };

        var editBtn = new TextBlock
        {
            Text = "✎",
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("TextMuted"),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Click to type a value"
        };

        void BeginEdit()
        {
            _qtyEdit.Text = _qty.ToString();
            _qtyDisplay.Visibility = Visibility.Collapsed;
            editBtn.Visibility = Visibility.Collapsed;
            _qtyEdit.Visibility = Visibility.Visible;
            _qtyEdit.Focus();
            _qtyEdit.SelectAll();
        }

        void CommitEdit()
        {
            if (int.TryParse(_qtyEdit.Text, out var v))
                _qty = Math.Max(1, Math.Min(999, v));
            _qtyDisplay.Text = _qty.ToString();
            _qtyEdit.Visibility = Visibility.Collapsed;
            _qtyDisplay.Visibility = Visibility.Visible;
            editBtn.Visibility = Visibility.Visible;
        }

        void AdjustQty(int delta)
        {
            _qty = Math.Max(1, Math.Min(999, _qty + delta));
            _qtyDisplay.Text = _qty.ToString();
        }

        _qtyDisplay.MouseLeftButtonDown += (_, me) => { AdjustQty(-1); me.Handled = true; };
        _qtyDisplay.MouseRightButtonDown += (_, me) => { AdjustQty(+1); me.Handled = true; };
        _qtyDisplay.PreviewMouseWheel += (_, we) =>
        {
            AdjustQty(we.Delta > 0 ? 1 : -1);
            we.Handled = true;
        };

        editBtn.MouseLeftButtonDown += (_, me) => { BeginEdit(); me.Handled = true; };

        _qtyEdit.PreviewTextInput += (_, te) => { te.Handled = !te.Text.All(char.IsDigit); };
        _qtyEdit.LostFocus += (_, _) => CommitEdit();
        _qtyEdit.PreviewKeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter || ke.Key == Key.Escape)
            { CommitEdit(); ke.Handled = true; return; }
            if (int.TryParse(_qtyEdit.Text, out var v))
            {
                if (ke.Key == Key.Up) { _qtyEdit.Text = Math.Min(999, v + 1).ToString(); ke.Handled = true; }
                if (ke.Key == Key.Down) { _qtyEdit.Text = Math.Max(1, v - 1).ToString(); ke.Handled = true; }
            }
        };

        var qtyGrid = new Grid { Width = 36, VerticalAlignment = VerticalAlignment.Center };
        qtyGrid.Children.Add(_qtyDisplay);
        qtyGrid.Children.Add(_qtyEdit);

        bottomRow.Children.Add(_checkBox);
        bottomRow.Children.Add(qtyGrid);
        bottomRow.Children.Add(editBtn);
        stack.Children.Add(bottomRow);

        border.Child = stack;
        Visual = border;
    }
}
