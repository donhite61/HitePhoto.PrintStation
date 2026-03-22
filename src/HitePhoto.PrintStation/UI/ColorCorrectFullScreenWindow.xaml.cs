using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HitePhoto.PrintStation.Core.Models;
using HitePhoto.PrintStation.UI.ViewModels;

namespace HitePhoto.PrintStation.UI
{
    public partial class ColorCorrectFullScreenWindow : Window
    {
        private readonly ImageCorrectionCard _card;
        private readonly ColorCorrectWindowViewModel _parentVm;

        public ColorCorrectFullScreenWindow(
            ImageCorrectionCard card,
            ColorCorrectWindowViewModel parentVm)
        {
            InitializeComponent();
            _card     = card;
            _parentVm = parentVm;
            DataContext = card;
        }

        // ── Keyboard ──────────────────────────────────────────────────────────

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            bool alt      = (Keyboard.Modifiers & ModifierKeys.Alt)   != 0;
            bool shift    = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool altShift = alt && shift;

            if (e.Key == Key.Escape || e.Key == Key.Enter)
            {
                Close(); e.Handled = true; return;
            }

            if (!alt) return;

            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                int sign  = e.Key == Key.Up ? 1 : -1;
                int delta = altShift ? sign * 5 : sign;
                if (_card.FocusedField != null)
                    _card.State.AdjustField(_card.FocusedField, delta);
                e.Handled = true; return;
            }

            // Alt+letter: select correction field via registry lookup
            var actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if (ControlRegistry.ByShortcut.TryGetValue(actualKey, out var def))
            {
                _card.FocusedField = def.FieldName;
                e.Handled = true;
            }
        }

        private void Done_Click(object sender, RoutedEventArgs e) => Close();

        // ── Slot clicks ─────────────────────────────────────────────────────

        private void Slot_LeftClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is SlotViewModel slot)
            {
                _card.FocusedField = slot.Definition.FieldName;
                _card.State.AdjustField(slot.Definition.FieldName, +1);
                e.Handled = true;
            }
        }

        private void Slot_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is SlotViewModel slot)
            {
                _card.FocusedField = slot.Definition.FieldName;
                _card.State.AdjustField(slot.Definition.FieldName, -1);
                e.Handled = true;
            }
        }

        // ── Crop box ──────────────────────────────────────────────────────────

        private void FullImage_SizeChanged(object sender, SizeChangedEventArgs e)
            => DeferFullCropBoxUpdate();

        private void FullImage_BitmapUpdated(object sender, System.Windows.Data.DataTransferEventArgs e)
            => DeferFullCropBoxUpdate();

        private void DeferFullCropBoxUpdate()
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                UpdateFullCropBox(FullImage));
        }

        private void UpdateFullCropBox(Image img)
        {
            if (_card.OriginalPixelWidth == 0 || string.IsNullOrEmpty(_card.PrintSizeLabel)) return;

            ParsePrintSize(_card.PrintSizeLabel, out double printW, out double printH);
            if (printW <= 0 || printH <= 0) return;

            // Use parent Grid size as the viewport (Image control may report bitmap size)
            var viewport = VisualTreeHelper.GetParent(img) as FrameworkElement ?? img;
            double ctrlW = viewport.ActualWidth;
            double ctrlH = viewport.ActualHeight;
            if (ctrlW <= 0 || ctrlH <= 0) return;

            // Use the actual displayed bitmap aspect (accounts for portrait→landscape rotation)
            double imgAspect;
            if (img.Source is BitmapSource bmp && bmp.PixelWidth > 0)
                imgAspect = (double)bmp.PixelWidth / bmp.PixelHeight;
            else if (_card.OriginalPixelWidth > 0)
                imgAspect = (double)_card.OriginalPixelWidth / _card.OriginalPixelHeight;
            else
                return;

            double printAspect = printW / printH;

            // Auto-rotate print aspect to match displayed image orientation
            if ((imgAspect > 1.0 && printAspect < 1.0) || (imgAspect < 1.0 && printAspect > 1.0))
                printAspect = 1.0 / printAspect;

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

            double offX = (ctrlW - rendW) / 2;
            double offY = (ctrlH - rendH) / 2;

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

            FullCropRect.Margin     = new Thickness(cropX, cropY, 0, 0);
            FullCropRect.Width      = cropW;
            FullCropRect.Height     = cropH;
            FullCropRect.Visibility = Visibility.Visible;
        }

        private static void ParsePrintSize(string label, out double w, out double h)
        {
            w = 4; h = 6;
            var match = System.Text.RegularExpressions.Regex.Match(
                label, @"(\d+\.?\d*)\s*x\s*(\d+\.?\d*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success &&
                double.TryParse(match.Groups[1].Value, out w) &&
                double.TryParse(match.Groups[2].Value, out h))
                return;
            w = 4; h = 6;
        }
    }
}
