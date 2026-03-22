using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HitePhoto.PrintStation.Core.Models
{
    /// <summary>
    /// Holds all correction values for a single image.
    /// All values are integers -10 to +10. Default 0 (no adjustment).
    /// Exposure is a meta-control that fans out to Brightness/Contrast/Shadows/Highlights
    /// via configurable ratios in AppSettings.ExposureRatios.
    /// </summary>
    public class ImageCorrectionState : INotifyPropertyChanged
    {
        private string _imagePath     = string.Empty;
        private string _correctedPath = string.Empty;
        private bool   _skip;
        private bool   _isFocused;

        private int _exposure;
        private int _brightness;
        private int _contrast;
        private int _shadows;
        private int _highlights;
        private int _saturation;
        private int _colorTemp;
        private int _red;
        private int _green;
        private int _blue;

        // Advanced corrections
        private int _sigmoidalContrast;
        private int _clahe;
        private int _contrastStretch;
        private int _levels;

        // Toggle actions
        private bool _autoLevel;
        private bool _autoGamma;
        private bool _whiteBalance;
        private bool _normalize;
        private bool _grayscale;
        private bool _sepia;

        public string ImagePath
        {
            get => _imagePath;
            set { _imagePath = value; OnPropertyChanged(); }
        }

        public string CorrectedPath
        {
            get => _correctedPath;
            set { _correctedPath = value; OnPropertyChanged(); }
        }

        public bool Skip
        {
            get => _skip;
            set { _skip = value; OnPropertyChanged(); OnPropertyChanged(nameof(SkipOverlayVisible)); }
        }

        public bool IsFocused
        {
            get => _isFocused;
            set { _isFocused = value; OnPropertyChanged(); }
        }

        public bool SkipOverlayVisible => _skip;

        // ── Correction values ─────────────────────────────────────────────────

        /// <summary>
        /// Meta-control: fans out to Brightness/Contrast/Shadows/Highlights
        /// via ExposureRatios in AppSettings. Does not get baked directly —
        /// the fan-out values are what ImageProcessor receives.
        /// </summary>
        public int Exposure
        {
            get => _exposure;
            set { _exposure = Clamp(value); OnPropertyChanged(); }
        }

        public int Brightness
        {
            get => _brightness;
            set { _brightness = Clamp(value); OnPropertyChanged(); }
        }

        public int Contrast
        {
            get => _contrast;
            set { _contrast = Clamp(value); OnPropertyChanged(); }
        }

        public int Shadows
        {
            get => _shadows;
            set { _shadows = Clamp(value); OnPropertyChanged(); }
        }

        public int Highlights
        {
            get => _highlights;
            set { _highlights = Clamp(value); OnPropertyChanged(); }
        }

        public int Saturation
        {
            get => _saturation;
            set { _saturation = Clamp(value); OnPropertyChanged(); }
        }

        public int ColorTemp
        {
            get => _colorTemp;
            set { _colorTemp = Clamp(value); OnPropertyChanged(); }
        }

        public int Red
        {
            get => _red;
            set { _red = Clamp(value); OnPropertyChanged(); }
        }

        public int Green
        {
            get => _green;
            set { _green = Clamp(value); OnPropertyChanged(); }
        }

        public int Blue
        {
            get => _blue;
            set { _blue = Clamp(value); OnPropertyChanged(); }
        }

        // ── Advanced corrections ────────────────────────────────────────────────

        public int SigmoidalContrast
        {
            get => _sigmoidalContrast;
            set { _sigmoidalContrast = Clamp(value); OnPropertyChanged(); }
        }

        public int Clahe
        {
            get => _clahe;
            set { _clahe = Clamp(value); OnPropertyChanged(); }
        }

        public int ContrastStretch
        {
            get => _contrastStretch;
            set { _contrastStretch = Clamp(value); OnPropertyChanged(); }
        }

        public int Levels
        {
            get => _levels;
            set { _levels = Clamp(value); OnPropertyChanged(); }
        }

        // ── Toggle actions ──────────────────────────────────────────────────────

        public bool AutoLevel
        {
            get => _autoLevel;
            set { _autoLevel = value; OnPropertyChanged(); }
        }

        public bool AutoGamma
        {
            get => _autoGamma;
            set { _autoGamma = value; OnPropertyChanged(); }
        }

        public bool WhiteBalance
        {
            get => _whiteBalance;
            set { _whiteBalance = value; OnPropertyChanged(); }
        }

        public bool Normalize
        {
            get => _normalize;
            set { _normalize = value; OnPropertyChanged(); }
        }

        public bool Grayscale
        {
            get => _grayscale;
            set { _grayscale = value; OnPropertyChanged(); }
        }

        public bool Sepia
        {
            get => _sepia;
            set { _sepia = value; OnPropertyChanged(); }
        }

        public bool IsAllZero =>
            Exposure == 0 && Brightness == 0 && Contrast == 0 &&
            Shadows == 0 && Highlights == 0 && Saturation == 0 &&
            ColorTemp == 0 && Red == 0 && Green == 0 && Blue == 0 &&
            SigmoidalContrast == 0 && Clahe == 0 && ContrastStretch == 0 && Levels == 0 &&
            !AutoLevel && !AutoGamma && !WhiteBalance && !Normalize && !Grayscale && !Sepia;

        public void Reset()
        {
            Exposure = Brightness = Contrast = Shadows = Highlights = 0;
            Saturation = ColorTemp = Red = Green = Blue = 0;
            SigmoidalContrast = Clahe = ContrastStretch = Levels = 0;
            AutoLevel = AutoGamma = WhiteBalance = Normalize = Grayscale = Sepia = false;
        }

        /// <summary>
        /// Apply a delta to the named correction field.
        /// Field names match property names exactly.
        /// </summary>
        public void AdjustField(string fieldName, int delta)
        {
            switch (fieldName)
            {
                case nameof(Exposure):    Exposure    += delta; break;
                case nameof(Brightness):  Brightness  += delta; break;
                case nameof(Contrast):    Contrast    += delta; break;
                case nameof(Shadows):     Shadows     += delta; break;
                case nameof(Highlights):  Highlights  += delta; break;
                case nameof(Saturation):  Saturation  += delta; break;
                case nameof(ColorTemp):   ColorTemp   += delta; break;
                case nameof(Red):                Red                += delta; break;
                case nameof(Green):              Green              += delta; break;
                case nameof(Blue):               Blue               += delta; break;
                case nameof(SigmoidalContrast):  SigmoidalContrast  += delta; break;
                case nameof(Clahe):              Clahe              += delta; break;
                case nameof(ContrastStretch):    ContrastStretch    += delta; break;
                case nameof(Levels):             Levels             += delta; break;
            }
        }

        private static int Clamp(int value) =>
            System.Math.Max(-10, System.Math.Min(10, value));

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
