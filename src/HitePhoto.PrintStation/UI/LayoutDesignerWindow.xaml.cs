using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HitePhoto.PrintStation.Core.Models;

namespace HitePhoto.PrintStation.UI;

public partial class LayoutDesignerWindow : Window
{
    private readonly List<ChannelInfo> _allChannels;
    private readonly AppSettings _settings;
    private readonly LayoutDefinition? _editingLayout;
    private bool _suppressRecalc;

    public LayoutDefinition? Result { get; private set; }

    public LayoutDesignerWindow(
        List<ChannelInfo> allChannels,
        AppSettings settings,
        LayoutDefinition? existing = null)
    {
        InitializeComponent();
        _allChannels = allChannels;
        _settings = settings;
        _editingLayout = existing;

        PopulateGridCombos();
        PopulateChannelCombo();

        if (existing != null)
            PopulateFromLayout(existing);
        else
            SetDefaults();
    }

    private void PopulateGridCombos()
    {
        var counts = Enumerable.Range(1, 10).Select(i => i.ToString()).ToList();
        RowsCombo.ItemsSource = counts;
        ColumnsCombo.ItemsSource = counts;
        RowsCombo.SelectedIndex = 0;
        ColumnsCombo.SelectedIndex = 0;
    }

    private void PopulateChannelCombo()
    {
        var items = _allChannels
            .Select(c => $"{c.ChannelNumber:D3}  {c.SizeLabel}  {c.MediaType}".Trim())
            .ToList();
        ChannelCombo.ItemsSource = items;
        if (items.Count > 0) ChannelCombo.SelectedIndex = 0;
    }

    private void SetDefaults()
    {
        _suppressRecalc = true;
        PrintWidthBox.Text = "2.5";
        PrintHeightBox.Text = "3.5";
        GapHBox.Text = "0";
        GapVBox.Text = "0";
        OffsetBeforeBox.Text = "0";
        MarginAroundBox.Text = "0";
        MarginLeftBox.Text = "0";
        MarginRightBox.Text = "0";
        MarginTopBox.Text = "0";
        MarginBottomBox.Text = "0";
        _suppressRecalc = false;
        Recalculate();
    }

    private void PopulateFromLayout(LayoutDefinition l)
    {
        _suppressRecalc = true;

        NameBox.Text = l.Name;
        PrintWidthBox.Text = l.PrintWidth.ToString(CultureInfo.InvariantCulture);
        PrintHeightBox.Text = l.PrintHeight.ToString(CultureInfo.InvariantCulture);

        RowsCombo.SelectedIndex = Math.Clamp(l.Rows - 1, 0, 9);
        ColumnsCombo.SelectedIndex = Math.Clamp(l.Columns - 1, 0, 9);

        GapHBox.Text = l.GapHorizontal.ToString(CultureInfo.InvariantCulture);
        GapVBox.Text = l.GapVertical.ToString(CultureInfo.InvariantCulture);
        OffsetBeforeBox.Text = l.OffsetBefore.ToString(CultureInfo.InvariantCulture);

        MarginLeftBox.Text = l.MarginLeft.ToString(CultureInfo.InvariantCulture);
        MarginRightBox.Text = l.MarginRight.ToString(CultureInfo.InvariantCulture);
        MarginTopBox.Text = l.MarginTop.ToString(CultureInfo.InvariantCulture);
        MarginBottomBox.Text = l.MarginBottom.ToString(CultureInfo.InvariantCulture);

        if (l.MarginLeft == l.MarginRight && l.MarginRight == l.MarginTop && l.MarginTop == l.MarginBottom)
            MarginAroundBox.Text = l.MarginLeft.ToString(CultureInfo.InvariantCulture);
        else
            MarginAroundBox.Text = "";

        AutoMatchBox.Text = string.Join(", ", l.AutoMatchSizes);

        var idx = _allChannels.FindIndex(c => c.ChannelNumber == l.TargetChannelNumber);
        if (idx >= 0) ChannelCombo.SelectedIndex = idx;

        _suppressRecalc = false;
        Recalculate();
    }

    // ── Field change handlers ─────────────────────────────────────────

    private void Field_Changed(object sender, TextChangedEventArgs e) => Recalculate();
    private void Field_SelectionChanged(object sender, SelectionChangedEventArgs e) => Recalculate();
    private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Recalculate();

    private void MarginAround_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppressRecalc) return;

        if (TryParseDouble(MarginAroundBox.Text, out double val))
        {
            _suppressRecalc = true;
            string s = val.ToString(CultureInfo.InvariantCulture);
            MarginLeftBox.Text = s;
            MarginRightBox.Text = s;
            MarginTopBox.Text = s;
            MarginBottomBox.Text = s;
            _suppressRecalc = false;
        }
        Recalculate();
    }

    // ── Recalculate + preview ─────────────────────────────────────────

    private void Recalculate()
    {
        if (_suppressRecalc) return;

        var layout = BuildLayoutFromFields();
        if (layout == null)
        {
            CalcPageSizeText.Text = "—";
            SizeMatchText.Text = "";
            return;
        }

        CalcPageSizeText.Text = layout.CalculatedPageSizeDisplay;

        if (ChannelCombo.SelectedIndex >= 0 && ChannelCombo.SelectedIndex < _allChannels.Count)
        {
            var ch = _allChannels[ChannelCombo.SelectedIndex];
            double chW = ch.WidthInches;
            double chH = ch.HeightInches;
            bool matchNormal = Math.Abs(layout.CalculatedPageWidth - chW) < 0.02 &&
                               Math.Abs(layout.CalculatedPageHeight - chH) < 0.02;
            bool matchFlipped = Math.Abs(layout.CalculatedPageWidth - chH) < 0.02 &&
                                Math.Abs(layout.CalculatedPageHeight - chW) < 0.02;

            if (matchNormal || matchFlipped)
            {
                SizeMatchText.Text = "Matches channel size";
                SizeMatchText.Foreground = (Brush)FindResource("AccentGreen");
                SaveBtn.IsEnabled = true;
            }
            else
            {
                SizeMatchText.Text = $"Does not match channel ({chW}x{chH})";
                SizeMatchText.Foreground = (Brush)FindResource("AccentRed");
                SaveBtn.IsEnabled = false;
            }
        }

        DrawPreview(layout);
    }

    private LayoutDefinition? BuildLayoutFromFields()
    {
        if (!TryParseDouble(PrintWidthBox.Text, out double pw) || pw <= 0) return null;
        if (!TryParseDouble(PrintHeightBox.Text, out double ph) || ph <= 0) return null;

        int rows = RowsCombo.SelectedIndex + 1;
        int cols = ColumnsCombo.SelectedIndex + 1;

        TryParseDouble(GapHBox.Text, out double gapH);
        TryParseDouble(GapVBox.Text, out double gapV);
        TryParseDouble(OffsetBeforeBox.Text, out double offset);
        TryParseDouble(MarginLeftBox.Text, out double mL);
        TryParseDouble(MarginRightBox.Text, out double mR);
        TryParseDouble(MarginTopBox.Text, out double mT);
        TryParseDouble(MarginBottomBox.Text, out double mB);

        return new LayoutDefinition
        {
            PrintWidth = pw, PrintHeight = ph,
            Rows = rows, Columns = cols,
            GapHorizontal = gapH, GapVertical = gapV,
            OffsetBefore = offset,
            MarginLeft = mL, MarginRight = mR,
            MarginTop = mT, MarginBottom = mB,
        };
    }

    // ── Preview drawing ───────────────────────────────────────────────

    private void DrawPreview(LayoutDefinition layout)
    {
        PreviewCanvas.Children.Clear();

        double canvasW = PreviewCanvas.ActualWidth;
        double canvasH = PreviewCanvas.ActualHeight;
        if (canvasW <= 0 || canvasH <= 0) return;

        double pageW = layout.CalculatedPageWidth;
        double pageH = layout.CalculatedPageHeight;
        if (pageW <= 0 || pageH <= 0) return;

        double pad = 20;
        double scale = Math.Min((canvasW - pad * 2) / pageW, (canvasH - pad * 2) / pageH);
        double drawW = pageW * scale;
        double drawH = pageH * scale;
        double ox = (canvasW - drawW) / 2;
        double oy = (canvasH - drawH) / 2;

        var paper = new Rectangle
        {
            Width = drawW, Height = drawH,
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            StrokeThickness = 1
        };
        Canvas.SetLeft(paper, ox);
        Canvas.SetTop(paper, oy);
        PreviewCanvas.Children.Add(paper);

        var cellBrush = new SolidColorBrush(Color.FromRgb(0x70, 0x80, 0xB0));
        var cellStroke = new SolidColorBrush(Color.FromRgb(0x50, 0x60, 0x90));

        for (int row = 0; row < layout.Rows; row++)
        {
            for (int col = 0; col < layout.Columns; col++)
            {
                double cx = layout.MarginLeft + col * (layout.PrintWidth + layout.GapHorizontal);
                double cy = layout.MarginTop + layout.OffsetBefore + row * (layout.PrintHeight + layout.GapVertical);

                var cell = new Rectangle
                {
                    Width = layout.PrintWidth * scale,
                    Height = layout.PrintHeight * scale,
                    Fill = cellBrush, Stroke = cellStroke,
                    StrokeThickness = 1, Opacity = 0.7
                };
                Canvas.SetLeft(cell, ox + cx * scale);
                Canvas.SetTop(cell, oy + cy * scale);
                PreviewCanvas.Children.Add(cell);

                var label = new TextBlock
                {
                    Text = $"{layout.PrintWidth}x{layout.PrintHeight}",
                    FontSize = Math.Max(9, Math.Min(14, scale * 0.6)),
                    Foreground = Brushes.White,
                };
                Canvas.SetLeft(label, ox + cx * scale + 4);
                Canvas.SetTop(label, oy + cy * scale + 4);
                PreviewCanvas.Children.Add(label);
            }
        }

        var dimBrush = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        var widthLabel = new TextBlock { Text = $"{pageW:F3}\"", FontSize = 11, Foreground = dimBrush };
        Canvas.SetLeft(widthLabel, ox + drawW / 2 - 20);
        Canvas.SetTop(widthLabel, oy + drawH + 4);
        PreviewCanvas.Children.Add(widthLabel);

        var heightLabel = new TextBlock
        {
            Text = $"{pageH:F3}\"", FontSize = 11, Foreground = dimBrush,
            RenderTransform = new RotateTransform(-90)
        };
        Canvas.SetLeft(heightLabel, ox - 18);
        Canvas.SetTop(heightLabel, oy + drawH / 2 + 15);
        PreviewCanvas.Children.Add(heightLabel);
    }

    // ── Save / Cancel ─────────────────────────────────────────────────

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("Enter a layout name.", "Missing Name",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }

        var layout = BuildLayoutFromFields();
        if (layout == null)
        {
            MessageBox.Show("Invalid print size values.", "Invalid Layout",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_editingLayout == null || !_editingLayout.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            var exists = _settings.Layouts?.Any(l =>
                l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? false;
            if (exists)
            {
                if (MessageBox.Show($"A layout named '{name}' already exists. Overwrite it?",
                        "Duplicate Name", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    != MessageBoxResult.Yes)
                    return;
            }
        }

        layout.Name = name;

        if (ChannelCombo.SelectedIndex >= 0 && ChannelCombo.SelectedIndex < _allChannels.Count)
        {
            var ch = _allChannels[ChannelCombo.SelectedIndex];
            layout.TargetChannelNumber = ch.ChannelNumber;
            layout.TargetSizeLabel = ch.SizeLabel;
        }

        layout.AutoMatchSizes = AutoMatchBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        Result = layout;
        DialogResult = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
