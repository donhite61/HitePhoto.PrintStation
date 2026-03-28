using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Core.Models;
using HitePhoto.PrintStation.Core.Processing;
using HitePhoto.PrintStation.Data;
using System.Windows.Input;
using ImageMagick;


namespace HitePhoto.PrintStation.UI.ViewModels
{
    // ── ImageCorrectionCard ───────────────────────────────────────────────────

    public class ImageCorrectionCard : ViewModelBase
    {
        private readonly AppSettings _settings;
        internal AppSettings Settings => _settings;
        private readonly Dispatcher _dispatcher;

        private ImageCorrectionState _state;
        private BitmapSource? _previewBitmap;
        private byte[]? _originalBytes;
        private byte[]? _previewSourceBytes;
        private bool _originalLoaded;
        private string? _focusedField;
        private bool _isRendering;

        private CancellationTokenSource? _debounce;
        private bool _isPreviewRotated;

        public int OriginalPixelWidth  { get; private set; }
        public int OriginalPixelHeight { get; private set; }

        public string PrintSizeLabel { get; set; } = string.Empty;

        public string DimensionLabel =>
            OriginalPixelWidth > 0
                ? $"{OriginalPixelWidth}×{OriginalPixelHeight}"
                : string.Empty;

        public string ZoomLabel
        {
            get
            {
                if (OriginalPixelWidth == 0 || string.IsNullOrEmpty(PrintSizeLabel))
                    return string.Empty;
                ParsePrintSize(PrintSizeLabel, out double pw, out double ph);
                if (pw <= 0 || ph <= 0) return string.Empty;
                double printW = pw * 300;
                double printH = ph * 300;
                double zoomW  = OriginalPixelWidth  / printW * 100;
                double zoomH  = OriginalPixelHeight / printH * 100;
                double zoom   = Math.Min(zoomW, zoomH);
                return $"{zoom:F1}%";
            }
        }

        public Rect CropBoxRect { get; set; } = Rect.Empty;

        public ImageCorrectionState State
        {
            get => _state;
            set => SetField(ref _state, value);
        }

        public string? FocusedField
        {
            get => _focusedField;
            set => SetField(ref _focusedField, value);
        }

        public BitmapSource? PreviewBitmap
        {
            get => _previewBitmap;
            private set => SetField(ref _previewBitmap, value);
        }

        public bool IsRendering
        {
            get => _isRendering;
            set => SetField(ref _isRendering, value);
        }

        public ObservableCollection<SlotViewModel> TopSlots       { get; } = new();
        public ObservableCollection<SlotViewModel> BottomRow1Slots { get; } = new();
        public ObservableCollection<SlotViewModel> BottomRow2Slots { get; } = new();
        public ObservableCollection<SlotViewModel> FullScreenSlots { get; } = new();

        public ImageCorrectionCard(ImageCorrectionState state, AppSettings settings, Dispatcher dispatcher)
        {
            _state      = state;
            _settings   = settings;
            _dispatcher = dispatcher;
            _focusedField = "Exposure";

            state.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ImageCorrectionState.Skip) or
                    nameof(ImageCorrectionState.IsFocused) or
                    nameof(ImageCorrectionState.CorrectedPath)) return;
                SchedulePreviewUpdate();
            };

            InitializeSlots(settings.CorrectionSlotLayout ?? ControlRegistry.DefaultSlotLayout());
        }

        public void EnsureOriginalLoaded()
        {
            if (_originalLoaded) return;
            _originalLoaded = true;

            Task.Run(() =>
            {
                if (!File.Exists(State.ImagePath)) return;
                try
                {
                    _originalBytes = File.ReadAllBytes(State.ImagePath);

                    using var info = new MagickImage(_originalBytes);
                    info.AutoOrient();
                    int origW = (int)info.Width;
                    int origH = (int)info.Height;

                    bool rotated = info.Height > info.Width;
                    if (rotated)
                        info.Rotate(90);

                    if (info.Width > 600)
                        info.Resize(600, 0);

                    _previewSourceBytes = info.ToByteArray(MagickFormat.Bmp);

                    var bitmapSource = MagickImageToBitmapSource(info);

                    _dispatcher.Invoke(() =>
                    {
                        OriginalPixelWidth  = origW;
                        OriginalPixelHeight = origH;
                        _isPreviewRotated   = rotated;
                        OnPropertyChanged(nameof(DimensionLabel));
                        OnPropertyChanged(nameof(ZoomLabel));

                        _previewBitmap = bitmapSource;
                        OnPropertyChanged(nameof(PreviewBitmap));
                    });
                }
                catch { }
            });
        }

        public void SchedulePreviewUpdate()
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = new CancellationTokenSource();
            var token = _debounce.Token;

            Task.Delay(100, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                RenderPreview();
            });
        }

        private void RenderPreview()
        {
            var previewBytes = _previewSourceBytes;
            if (previewBytes == null) return;

            var state      = State;
            var strengths  = _settings.CorrectionStrengths;
            var parameters = _settings.ControlParameters;

            _dispatcher.Invoke(() => IsRendering = true);

            Task.Run(() =>
            {
                try
                {
                    using var image = new MagickImage(previewBytes);

                    ImageProcessor.ApplyCorrectionPipeline(
                        image, state.Exposure,
                        state.Brightness, state.Contrast, state.Shadows, state.Highlights,
                        state.Saturation, state.ColorTemp,
                        state.Red, state.Green, state.Blue,
                        strengths, state, parameters);

                    var bitmapSource = MagickImageToBitmapSource(image);

                    _dispatcher.Invoke(() =>
                    {
                        _previewBitmap = bitmapSource;
                        IsRendering = false;
                        OnPropertyChanged(nameof(PreviewBitmap));
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Info($"Preview render failed — {ex.Message}");
                    AlertCollector.Error(AlertCategory.DataQuality,
                        "Color correction preview render failed",
                        detail: $"Attempted: render preview. Expected: preview image. Found: {ex.Message}. " +
                                $"Context: color correction window. State: preview not shown.",
                        ex: ex);
                    _dispatcher.Invoke(() => IsRendering = false);
                }
            });
        }

        public void Cleanup()
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = null;

            foreach (var slot in TopSlots)       slot.Detach();
            foreach (var slot in BottomRow1Slots) slot.Detach();
            foreach (var slot in BottomRow2Slots) slot.Detach();

            _originalBytes = null;
            _previewSourceBytes = null;
        }

        private void InitializeSlots(List<string> layout)
        {
            TopSlots.Clear();
            BottomRow1Slots.Clear();
            BottomRow2Slots.Clear();
            FullScreenSlots.Clear();

            while (layout.Count < 23) layout.Add("");

            for (int i = 0; i < 23; i++)
            {
                var id = layout[i];
                var def = !string.IsNullOrEmpty(id) && ControlRegistry.ById.TryGetValue(id, out var d)
                    ? d : ControlRegistry.Empty;
                var slot = new SlotViewModel(def, this);

                if (i < 7)        TopSlots.Add(slot);
                else if (i < 14)  BottomRow1Slots.Add(slot);
                else              BottomRow2Slots.Add(slot);

                if (def.Kind == SlotKind.Correction)
                    FullScreenSlots.Add(slot);
            }
        }

        private static BitmapSource MagickImageToBitmapSource(MagickImage image)
        {
            var bytes = image.ToByteArray(MagickFormat.Bmp);
            using var stream = new MemoryStream(bytes);
            var decoder = new BmpBitmapDecoder(stream,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
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

    // ── ColorCorrectWindowViewModel ───────────────────────────────────────────

    public class ColorCorrectWindowViewModel : ViewModelBase
    {
        private readonly string _orderId;
        private readonly string _orderFolderPath;
        private readonly string _sizeLabel;
        private readonly CorrectionStore _correctionStore;
        private readonly AppSettings _settings;
        private readonly Action<List<ImageCorrectionState>> _onPageConfirmed;
        private readonly Dispatcher _dispatcher;

        private readonly List<ImageCorrectionState> _allImages = new();
        private int  _pageIndex;
        private int  _focusedCardIndex;
        private bool _isComplete;
        private string _statusText = string.Empty;

        public ObservableCollection<ImageCorrectionCard> PageCards { get; } = new();

        public int FocusedCardIndex
        {
            get => _focusedCardIndex;
            set
            {
                int clamped = Math.Max(0, Math.Min(PageCards.Count - 1, value));
                if (_focusedCardIndex == clamped) return;
                if (_focusedCardIndex < PageCards.Count)
                    PageCards[_focusedCardIndex].State.IsFocused = false;
                _focusedCardIndex = clamped;
                if (PageCards.Count > 0)
                    PageCards[_focusedCardIndex].State.IsFocused = true;
                OnPropertyChanged();
            }
        }

        public bool IsComplete
        {
            get => _isComplete;
            set => SetField(ref _isComplete, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetField(ref _statusText, value);
        }

        public string PageLabel =>
            $"Page {_pageIndex + 1} of {TotalPages}  —  " +
            $"Images {_pageIndex * 6 + 1}–{Math.Min((_pageIndex + 1) * 6, _allImages.Count)} " +
            $"of {_allImages.Count}";

        public int TotalPages => Math.Max(1, (int)Math.Ceiling(_allImages.Count / 6.0));

        public RelayCommand ConfirmPageCommand { get; }
        public RelayCommand PassPageCommand    { get; }
        public RelayCommand SkipRestCommand    { get; }

        public event EventHandler? RequestClose;

        public ColorCorrectWindowViewModel(
            string orderId,
            string orderFolderPath,
            string sizeLabel,
            List<string> imagePaths,
            CorrectionStore correctionStore,
            AppSettings settings,
            Action<List<ImageCorrectionState>> onPageConfirmed)
        {
            _orderId         = orderId;
            _orderFolderPath = orderFolderPath;
            _sizeLabel       = sizeLabel;
            _correctionStore = correctionStore;
            _settings        = settings;
            _onPageConfirmed = onPageConfirmed;
            _dispatcher      = Dispatcher.CurrentDispatcher;

            ConfirmPageCommand = new RelayCommand(ConfirmPage);
            PassPageCommand    = new RelayCommand(PassPage);
            SkipRestCommand    = new RelayCommand(SkipRest);

            // Load saved corrections if this is a reprint
            var saved = correctionStore.GetCorrections(orderId)
                .ToDictionary(r => r.ImagePath, StringComparer.OrdinalIgnoreCase);

            foreach (var path in imagePaths)
            {
                var state = new ImageCorrectionState { ImagePath = path };

                if (saved.TryGetValue(path, out var rec))
                {
                    state.Exposure           = rec.Exposure;
                    state.Brightness         = rec.Brightness;
                    state.Contrast           = rec.Contrast;
                    state.Shadows            = rec.Shadows;
                    state.Highlights         = rec.Highlights;
                    state.Saturation         = rec.Saturation;
                    state.ColorTemp          = rec.ColorTemp;
                    state.Red                = rec.Red;
                    state.Green              = rec.Green;
                    state.Blue               = rec.Blue;
                    state.SigmoidalContrast  = rec.SigmoidalContrast;
                    state.Clahe              = rec.Clahe;
                    state.ContrastStretch    = rec.ContrastStretch;
                    state.Levels             = rec.Levels;
                    state.AutoLevel          = rec.AutoLevel;
                    state.AutoGamma          = rec.AutoGamma;
                    state.WhiteBalance       = rec.WhiteBalance;
                    state.Normalize          = rec.Normalize;
                    state.Grayscale          = rec.Grayscale;
                    state.Sepia              = rec.Sepia;
                    state.CorrectedPath      = rec.CorrectedPath;
                }

                _allImages.Add(state);
            }

            LoadPage(0);
        }

        private void LoadPage(int pageIndex)
        {
            _pageIndex = pageIndex;

            foreach (var card in PageCards)
                card.Cleanup();
            PageCards.Clear();

            var pageImages = _allImages.Skip(pageIndex * 6).Take(6).ToList();

            foreach (var state in pageImages)
            {
                var card = new ImageCorrectionCard(state, _settings, _dispatcher)
                {
                    PrintSizeLabel = _sizeLabel,
                    FocusedField   = "Exposure"
                };
                PageCards.Add(card);
            }

            _focusedCardIndex = 0;
            if (PageCards.Count > 0)
            {
                PageCards[0].State.IsFocused = true;
                foreach (var card in PageCards)
                    card.EnsureOriginalLoaded();
            }

            OnPropertyChanged(nameof(PageLabel));
            StatusText = string.Empty;
        }

        public void MoveFocus(int delta)
        {
            if (PageCards.Count == 0) return;
            int next = Math.Clamp(_focusedCardIndex + delta, 0, PageCards.Count - 1);
            if (next == _focusedCardIndex) return;
            PageCards[_focusedCardIndex].State.IsFocused = false;
            _focusedCardIndex = next;
            PageCards[_focusedCardIndex].State.IsFocused = true;
            OnPropertyChanged(nameof(FocusedCardIndex));
        }

        public void SetFocusedField(string fieldName)
        {
            if (_focusedCardIndex >= PageCards.Count) return;
            PageCards[_focusedCardIndex].FocusedField = fieldName;
        }

        public void AdjustFocusedValue(int delta)
        {
            if (_focusedCardIndex >= PageCards.Count) return;
            var card = PageCards[_focusedCardIndex];
            if (card.FocusedField == null) return;
            card.State.AdjustField(card.FocusedField, delta);
        }

        public void ToggleSkipFocused()
        {
            if (_focusedCardIndex >= PageCards.Count) return;
            var state = PageCards[_focusedCardIndex].State;
            state.Skip = !state.Skip;
        }

        public void ToggleSkip(int cardIndex)
        {
            if (cardIndex >= PageCards.Count) return;
            PageCards[cardIndex].State.Skip = !PageCards[cardIndex].State.Skip;
        }

        public void AdjustValue(int cardIndex, string fieldName, int delta)
        {
            if (cardIndex >= PageCards.Count) return;
            PageCards[cardIndex].State.AdjustField(fieldName, delta);
        }

        public ImageCorrectionCard? GetCard(int index) =>
            index >= 0 && index < PageCards.Count ? PageCards[index] : null;

        public bool HandlePresetClick(int cardIndex, string presetId)
        {
            if (cardIndex >= PageCards.Count) return false;

            if (_settings.Presets.TryGetValue(presetId, out var preset))
            {
                preset.ApplyTo(PageCards[cardIndex].State);
                StatusText = $"Applied preset {presetId}";
                return true;
            }
            else
            {
                StatusText = $"Preset {presetId} — empty (hold to save)";
                return false;
            }
        }

        public void SavePreset(int cardIndex, string presetId)
        {
            if (cardIndex >= PageCards.Count) return;
            var state = PageCards[cardIndex].State;
            _settings.Presets[presetId] = PresetDefinition.FromState(state);

            // Persist settings
            new SettingsManager().Save(_settings);

            StatusText = $"Saved to {presetId}";

            foreach (var card in PageCards)
                NotifyPresetSlots(card, presetId);
        }

        public void HandleActionClick(int cardIndex, string actionId)
        {
            if (cardIndex >= PageCards.Count) return;
            var card = PageCards[cardIndex];
            var state = card.State;

            switch (actionId)
            {
                case "RST":
                    state.Reset();
                    StatusText = "Corrections reset";
                    break;
                case "HLD":
                    StatusText = "Hold — not yet implemented";
                    break;
                case "BW":
                    state.Grayscale = !state.Grayscale;
                    if (state.Grayscale) state.Sepia = false;
                    StatusText = state.Grayscale ? "Grayscale ON" : "Grayscale OFF";
                    break;
                case "AL":
                    state.AutoLevel = !state.AutoLevel;
                    StatusText = state.AutoLevel ? "AutoLevel ON" : "AutoLevel OFF";
                    break;
                case "AG":
                    state.AutoGamma = !state.AutoGamma;
                    StatusText = state.AutoGamma ? "AutoGamma ON" : "AutoGamma OFF";
                    break;
                case "WB":
                    state.WhiteBalance = !state.WhiteBalance;
                    StatusText = state.WhiteBalance ? "White Balance ON" : "White Balance OFF";
                    break;
                case "NM":
                    state.Normalize = !state.Normalize;
                    StatusText = state.Normalize ? "Normalize ON" : "Normalize OFF";
                    break;
                case "SP":
                    state.Sepia = !state.Sepia;
                    if (state.Sepia) state.Grayscale = false;
                    StatusText = state.Sepia ? "Sepia ON" : "Sepia OFF";
                    break;
            }
        }

        private void ConfirmPage()
        {
            var toProcess = PageCards.Where(c => !c.State.Skip).Select(c => c.State).ToList();
            StatusText = $"Processing {toProcess.Count} image(s)...";

            var strengths  = _settings.CorrectionStrengths;
            var parameters = _settings.ControlParameters;

            foreach (var state in toProcess)
            {
                try
                {
                    string correctedPath = ImageProcessor.BuildCorrectedPath(state.ImagePath, _orderFolderPath);

                    if (!state.IsAllZero)
                    {
                        ImageProcessor.ApplyCorrections(
                            state.ImagePath, correctedPath,
                            state.Exposure,
                            state.Brightness, state.Contrast, state.Shadows, state.Highlights,
                            state.Saturation, state.ColorTemp,
                            state.Red, state.Green, state.Blue,
                            strengths, state, parameters);
                        state.CorrectedPath = correctedPath;
                    }
                    else
                    {
                        state.CorrectedPath = state.ImagePath;
                    }

                    _correctionStore.SaveCorrection(_orderId, state);
                }
                catch (Exception ex)
                {
                    AlertCollector.Error(AlertCategory.Printing,
                        "Color correction failed",
                        orderId: _orderId,
                        detail: $"Attempted: apply corrections to '{Path.GetFileName(state.ImagePath)}'. Found: exception.",
                        ex: ex);
                    StatusText = $"Error: {Path.GetFileName(state.ImagePath)}: {ex.Message}";
                    return;
                }
            }

            _onPageConfirmed(toProcess);

            int nextPage = _pageIndex + 1;
            if (nextPage < TotalPages)
            {
                LoadPage(nextPage);
                StatusText = $"Page {_pageIndex + 1} ready";
            }
            else
            {
                IsComplete = true;
                StatusText = "All images processed.";
                RequestClose?.Invoke(this, EventArgs.Empty);
            }
        }

        private void PassPage()
        {
            foreach (var card in PageCards)
                card.State.Skip = true;
            ConfirmPage();
        }

        private void SkipRest()
        {
            IsComplete = true;
            StatusText = "Remaining images skipped (F12).";
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private static void NotifyPresetSlots(ImageCorrectionCard card, string presetId)
        {
            foreach (var slot in card.TopSlots)
                if (slot.Definition.Id == presetId)
                    slot.RefreshPresetState();
            foreach (var slot in card.BottomRow1Slots)
                if (slot.Definition.Id == presetId)
                    slot.RefreshPresetState();
            foreach (var slot in card.BottomRow2Slots)
                if (slot.Definition.Id == presetId)
                    slot.RefreshPresetState();
        }
    }
}
