namespace HitePhoto.PrintStation.Core.Models
{
    /// <summary>
    /// Stores a saved correction preset — all values from ImageCorrectionState.
    /// </summary>
    public class PresetDefinition
    {
        // Adjustable corrections (-10..+10)
        public int Exposure          { get; set; }
        public int Brightness        { get; set; }
        public int Contrast          { get; set; }
        public int Shadows           { get; set; }
        public int Highlights        { get; set; }
        public int Saturation        { get; set; }
        public int ColorTemp         { get; set; }
        public int Red               { get; set; }
        public int Green             { get; set; }
        public int Blue              { get; set; }
        public int SigmoidalContrast { get; set; }
        public int Clahe             { get; set; }
        public int ContrastStretch   { get; set; }
        public int Levels            { get; set; }

        // Toggle actions
        public bool AutoLevel    { get; set; }
        public bool AutoGamma    { get; set; }
        public bool WhiteBalance { get; set; }
        public bool Normalize    { get; set; }
        public bool Grayscale    { get; set; }
        public bool Sepia        { get; set; }

        /// <summary>
        /// Capture current state into a preset definition.
        /// </summary>
        public static PresetDefinition FromState(ImageCorrectionState s) => new()
        {
            Exposure          = s.Exposure,
            Brightness        = s.Brightness,
            Contrast          = s.Contrast,
            Shadows           = s.Shadows,
            Highlights        = s.Highlights,
            Saturation        = s.Saturation,
            ColorTemp         = s.ColorTemp,
            Red               = s.Red,
            Green             = s.Green,
            Blue              = s.Blue,
            SigmoidalContrast = s.SigmoidalContrast,
            Clahe             = s.Clahe,
            ContrastStretch   = s.ContrastStretch,
            Levels            = s.Levels,
            AutoLevel         = s.AutoLevel,
            AutoGamma         = s.AutoGamma,
            WhiteBalance      = s.WhiteBalance,
            Normalize         = s.Normalize,
            Grayscale         = s.Grayscale,
            Sepia             = s.Sepia,
        };

        /// <summary>
        /// Apply this preset's values to a correction state.
        /// </summary>
        public void ApplyTo(ImageCorrectionState s)
        {
            s.Exposure          = Exposure;
            s.Brightness        = Brightness;
            s.Contrast          = Contrast;
            s.Shadows           = Shadows;
            s.Highlights        = Highlights;
            s.Saturation        = Saturation;
            s.ColorTemp         = ColorTemp;
            s.Red               = Red;
            s.Green             = Green;
            s.Blue              = Blue;
            s.SigmoidalContrast = SigmoidalContrast;
            s.Clahe             = Clahe;
            s.ContrastStretch   = ContrastStretch;
            s.Levels            = Levels;
            s.AutoLevel         = AutoLevel;
            s.AutoGamma         = AutoGamma;
            s.WhiteBalance      = WhiteBalance;
            s.Normalize         = Normalize;
            s.Grayscale         = Grayscale;
            s.Sepia             = Sepia;
        }
    }
}
