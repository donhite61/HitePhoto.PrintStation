using System;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using HitePhoto.PrintStation.Core.Models;
using HitePhoto.PrintStation.UI.ViewModels;

namespace HitePhoto.PrintStation.UI
{
    public partial class ColorCorrectWindow : Window
    {
        private ColorCorrectWindowViewModel _vm = null!;

        // Press-and-hold preset save
        private DispatcherTimer? _presetHoldTimer;
        private string? _presetHoldId;
        private int _presetHoldCardIndex;

        public ColorCorrectWindow()
        {
            InitializeComponent();
            DataContextChanged += (_, _) =>
            {
                if (DataContext is ColorCorrectWindowViewModel vm)
                {
                    _vm = vm;
                    _vm.RequestClose += (_, _) => Close();
                }
            };
        }

        // ── Keyboard handling ─────────────────────────────────────────────────

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (_vm == null) return;

            bool alt      = (Keyboard.Modifiers & ModifierKeys.Alt)   != 0;
            bool shift    = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool altShift = alt && shift;

            if (e.Key == Key.F12)
            {
                _vm.SkipRestCommand.Execute(null);
                e.Handled = true; return;
            }

            if (e.Key == Key.Next) // PgDn
            {
                _vm.PassPageCommand.Execute(null);
                e.Handled = true; return;
            }

            if (e.Key == Key.Enter)
            {
                _vm.ConfirmPageCommand.Execute(null);
                e.Handled = true; return;
            }

            // ← → arrow keys move image focus (no modifier)
            if (!alt && e.Key == Key.Left)  { _vm.MoveFocus(-1); e.Handled = true; return; }
            if (!alt && e.Key == Key.Right) { _vm.MoveFocus(+1); e.Handled = true; return; }
            // ↑ ↓ also move between rows
            if (!alt && e.Key == Key.Up)    { _vm.MoveFocus(-3); e.Handled = true; return; }
            if (!alt && e.Key == Key.Down)  { _vm.MoveFocus(+3); e.Handled = true; return; }

            if (!alt) return;

            // Alt+1–6: select image by position
            if (e.Key >= Key.D1 && e.Key <= Key.D6)
            {
                _vm.FocusedCardIndex = e.Key - Key.D1;
                e.Handled = true; return;
            }

            // Alt+↑/↓: adjust value ±1 or ±5 with shift
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                int sign  = e.Key == Key.Up ? 1 : -1;
                int delta = altShift ? sign * 5 : sign;
                _vm.AdjustFocusedValue(delta);
                e.Handled = true; return;
            }

            // Alt+X: skip focused image
            if (e.Key == Key.X)
            {
                _vm.ToggleSkipFocused();
                e.Handled = true; return;
            }

            // Alt+letter: select correction field via registry lookup
            var actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if (ControlRegistry.ByShortcut.TryGetValue(actualKey, out var def))
            {
                _vm.SetFocusedField(def.FieldName);
                e.Handled = true;
            }
        }

        // ── Mouse: slot clicks (correction values) ─────────────────────────────

        private void Slot_LeftClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el) return;
            if (el.DataContext is not SlotViewModel slot) return;
            int idx = GetCardIndex(el);
            if (idx < 0) return;
            var card = _vm.GetCard(idx);
            if (card != null) card.FocusedField = slot.Definition.FieldName;
            _vm.AdjustValue(idx, slot.Definition.FieldName, +1);
            e.Handled = true;
        }

        private void Slot_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el) return;
            if (el.DataContext is not SlotViewModel slot) return;
            int idx = GetCardIndex(el);
            if (idx < 0) return;
            var card = _vm.GetCard(idx);
            if (card != null) card.FocusedField = slot.Definition.FieldName;
            _vm.AdjustValue(idx, slot.Definition.FieldName, -1);
            e.Handled = true;
        }

        // ── Mouse: preset clicks (press-and-hold to save) ────────────────────

        private void Preset_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el) return;
            if (el.DataContext is not SlotViewModel slot) return;
            int idx = GetCardIndex(el);
            if (idx < 0) return;

            _presetHoldId = slot.Definition.Id;
            _presetHoldCardIndex = idx;

            _presetHoldTimer?.Stop();
            _presetHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _presetHoldTimer.Tick += (_, _) =>
            {
                _presetHoldTimer.Stop();
                // Long press → save preset
                _vm.SavePreset(_presetHoldCardIndex, _presetHoldId!);
                _presetHoldId = null; // prevent apply on release
                // Confirmation: beep + flash orange
                SystemSounds.Exclamation.Play();
                FlashBorder(el, Color.FromRgb(0xFF, 0x99, 0x00), 300);
            };
            _presetHoldTimer.Start();

            el.CaptureMouse();
            e.Handled = true;
        }

        private void Preset_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el) return;
            el.ReleaseMouseCapture();

            _presetHoldTimer?.Stop();
            _presetHoldTimer = null;

            // If _presetHoldId is still set, the timer didn't fire → short click → apply
            if (_presetHoldId != null)
            {
                bool applied = _vm.HandlePresetClick(_presetHoldCardIndex, _presetHoldId);
                _presetHoldId = null;
                // Confirmation: beep + flash green if preset existed
                if (applied)
                {
                    SystemSounds.Asterisk.Play();
                    FlashBorder(el, Color.FromRgb(0x44, 0xBB, 0x44), 200);
                }
            }

            e.Handled = true;
        }

        // ── Mouse: action clicks ───────────────────────────────────────────────

        private void Action_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el) return;
            if (el.DataContext is not SlotViewModel slot) return;
            int idx = GetCardIndex(el);
            if (idx < 0) return;
            _vm.HandleActionClick(idx, slot.Definition.Id);
            e.Handled = true;
        }

        // ── Mouse: image right-click = skip, double-click = full screen ───────

        private void Image_RightClick(object sender, MouseButtonEventArgs e)
        {
            int idx = GetCardIndex(sender as DependencyObject);
            if (idx < 0) return;
            _vm.ToggleSkip(idx);
            e.Handled = true;
        }

        private void Image_LeftClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2) return;

            int idx = GetCardIndex(sender as DependencyObject);
            if (idx < 0) return;

            var card = _vm.GetCard(idx);
            if (card == null) return;

            // Open full screen window for this card
            var fullScreen = new ColorCorrectFullScreenWindow(card, _vm);
            fullScreen.Owner = this;
            fullScreen.ShowDialog();

            e.Handled = true;
        }

        // ── Crop box: recalculate when image size changes ─────────────────────
        // WPF calls SizeChanged when the Image control is laid out or resized.
        // We walk up to find the card's Canvas and Rectangle and update them.

        private void PreviewImage_SizeChanged(object sender, SizeChangedEventArgs e)
            => DeferCropBoxUpdate(sender);

        private void PreviewImage_BitmapUpdated(object sender, System.Windows.Data.DataTransferEventArgs e)
            => DeferCropBoxUpdate(sender);

        private void DeferCropBoxUpdate(object sender)
        {
            if (sender is not Image img) return;
            // Defer to after layout pass completes — setting properties during
            // SizeChanged (which fires inside Arrange) gets overridden by WPF.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                var card = FindCardFromElement(img);
                if (card == null) return;

                var parent = VisualTreeHelper.GetParent(img);
                var cropRect = FindVisualChild<Rectangle>(parent);
                if (cropRect == null) return;

                UpdateCropBox(img, cropRect, card);
            });
        }

        private static void UpdateCropBox(Image img, Rectangle rect, ImageCorrectionCard card)
        {
            if (string.IsNullOrEmpty(card.PrintSizeLabel)) return;
            if (card.OriginalPixelWidth == 0)              return;

            ParsePrintSize(card.PrintSizeLabel, out double printW, out double printH);
            if (printW <= 0 || printH <= 0) return;

            // Use parent Grid size as the viewport (Image control may report bitmap size)
            var viewport = VisualTreeHelper.GetParent(img) as FrameworkElement ?? img;
            double ctrlW = viewport.ActualWidth;
            double ctrlH = viewport.ActualHeight;
            if (ctrlW <= 0 || ctrlH <= 0) return;

            // Use the actual displayed bitmap aspect (accounts for portrait→landscape rotation)
            double imgAspect;
            if (img.Source is System.Windows.Media.Imaging.BitmapSource bmp && bmp.PixelWidth > 0)
                imgAspect = (double)bmp.PixelWidth / bmp.PixelHeight;
            else if (card.OriginalPixelWidth > 0)
                imgAspect = (double)card.OriginalPixelWidth / card.OriginalPixelHeight;
            else
                return; // no image yet

            double printAspect = printW / printH;

            // Auto-rotate print aspect to match displayed image orientation (Noritsu auto-rotates)
            if ((imgAspect > 1.0 && printAspect < 1.0) || (imgAspect < 1.0 && printAspect > 1.0))
                printAspect = 1.0 / printAspect;

            // Rendered image size inside the control (Stretch=Uniform)
            double rendW, rendH;
            if (ctrlW / ctrlH > imgAspect)
            {
                rendH = ctrlH;
                rendW = ctrlH * imgAspect;
            }
            else
            {
                rendW = ctrlW;
                rendH = ctrlW / imgAspect;
            }

            // Offset of rendered image within the control (centered by Stretch=Uniform)
            double offX = (ctrlW - rendW) / 2;
            double offY = (ctrlH - rendH) / 2;

            // Crop box: print aspect inscribed within the rendered image, centered
            double cropW, cropH;
            if (rendW / rendH > printAspect)
            {
                cropH = rendH;
                cropW = rendH * printAspect;
            }
            else
            {
                cropW = rendW;
                cropH = rendW / printAspect;
            }

            double cropX = offX + (rendW - cropW) / 2;
            double cropY = offY + (rendH - cropH) / 2;

            // Hide crop box when it covers 98%+ of the image (no meaningful crop)
            if (cropW >= rendW * 0.98 && cropH >= rendH * 0.98)
            {
                rect.Visibility = Visibility.Collapsed;
                return;
            }

            rect.Margin     = new Thickness(cropX, cropY, 0, 0);
            rect.Width      = cropW;
            rect.Height     = cropH;
            rect.Visibility = Visibility.Visible;
        }

        // ── Visual tree helpers ───────────────────────────────────────────────

        private int GetCardIndex(DependencyObject? element)
        {
            var card = FindCardFromElement(element);
            return card != null ? _vm.PageCards.IndexOf(card) : -1;
        }

        private static ImageCorrectionCard? FindCardFromElement(DependencyObject? element)
        {
            var current = element;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.DataContext is ImageCorrectionCard card)
                    return card;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private static void ParsePrintSize(string label, out double w, out double h)
        {
            // Extract the NxM dimensions from labels like "3.5x5 As-Is", "2.5x3.5 WALLET", "4x6 Luster"
            // Find the first "NxM" pattern by splitting on 'x' and stripping trailing non-numeric text
            w = 4; h = 6; // fallback
            var match = System.Text.RegularExpressions.Regex.Match(
                label, @"(\d+\.?\d*)\s*x\s*(\d+\.?\d*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success &&
                double.TryParse(match.Groups[1].Value, out w) &&
                double.TryParse(match.Groups[2].Value, out h))
                return;
            w = 4; h = 6;
        }

        /// <summary>
        /// Briefly flash a Border element's background to a color, then revert.
        /// </summary>
        private static void FlashBorder(DependencyObject element, Color flashColor, int durationMs)
        {
            // Walk up to find the Border that wraps this preset button
            var border = element as Border ?? FindParent<Border>(element);
            if (border == null) return;

            var original = border.Background;
            border.Background = new SolidColorBrush(flashColor);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
            timer.Tick += (_, _) =>
            {
                border.Background = original;
                timer.Stop();
            };
            timer.Start();
        }

        private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                child = VisualTreeHelper.GetParent(child);
                if (child is T match) return match;
            }
            return null;
        }
    }
}
