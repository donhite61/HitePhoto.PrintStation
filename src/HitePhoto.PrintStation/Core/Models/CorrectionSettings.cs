namespace HitePhoto.PrintStation.Core.Models
{
    /// <summary>
    /// Global strength multipliers for each correction type.
    /// 1.0 = standard curve impact. 2.0 = double effect per step.
    /// Range 0.1–3.0.
    /// </summary>
    public class CorrectionStrengths
    {
        public float Brightness { get; set; } = 1.0f;
        public float Contrast   { get; set; } = 1.0f;
        public float Shadows    { get; set; } = 1.0f;
        public float Highlights { get; set; } = 1.0f;
        public float Saturation { get; set; } = 1.0f;
        public float ColorTemp  { get; set; } = 1.0f;
        public float Red        { get; set; } = 1.0f;
        public float Green      { get; set; } = 1.0f;
        public float Blue               { get; set; } = 1.0f;
        public float SigmoidalContrast { get; set; } = 1.0f;
        public float Clahe             { get; set; } = 1.0f;
        public float ContrastStretch   { get; set; } = 1.0f;
        public float Levels            { get; set; } = 1.0f;
    }

    /// <summary>
    /// Advanced parameters for complex correction controls.
    /// </summary>
    public class ControlParameters
    {
        // Warmth: separate R and B channel strengths
        public float WarmthRed  { get; set; } = 1.0f;
        public float WarmthBlue { get; set; } = 1.0f;

        // SigmoidalContrast: midpoint (0-100%)
        public float SigmoidalMidpoint { get; set; } = 50f;

        // CLAHE: grid tiles and histogram bins
        public int ClaheXTiles { get; set; } = 8;
        public int ClaheYTiles { get; set; } = 8;
        public int ClaheBins   { get; set; } = 128;

        // ContrastStretch: black and white clip percentages
        public float ContrastStretchBlack { get; set; } = 0.5f;
        public float ContrastStretchWhite { get; set; } = 0.5f;

        // Level: black point, white point (percentages 0-100)
        public int LevelBlack { get; set; } = 0;
        public int LevelWhite { get; set; } = 100;
    }

    /// <summary>
    /// Ratios defining how a single Exposure integer fans out to
    /// Brightness, Contrast, Shadows, and Highlights.
    /// </summary>
    public class ExposureRatios
    {
        public float Brightness { get; set; } =  1.0f;
        public float Contrast   { get; set; } =  0.4f;
        public float Shadows    { get; set; } =  0.6f;
        public float Highlights { get; set; } = -0.3f;
    }

    /// <summary>
    /// A named snapshot of all correction strengths, exposure ratios,
    /// and control parameters. Allows switching between tuned profiles.
    /// </summary>
    public class CorrectionSettingsProfile
    {
        public string Name { get; set; } = string.Empty;
        public CorrectionStrengths Strengths { get; set; } = new();
        public ExposureRatios ExposureRatios { get; set; } = new();
        public ControlParameters Parameters { get; set; } = new();

        /// <summary>
        /// Capture the current settings into a new profile.
        /// </summary>
        public static CorrectionSettingsProfile FromSettings(string name, CorrectionStrengths s, ExposureRatios r, ControlParameters p)
        {
            return new CorrectionSettingsProfile
            {
                Name = name,
                Strengths = new CorrectionStrengths
                {
                    Brightness = s.Brightness, Contrast = s.Contrast,
                    Shadows = s.Shadows, Highlights = s.Highlights,
                    Saturation = s.Saturation, ColorTemp = s.ColorTemp,
                    Red = s.Red, Green = s.Green, Blue = s.Blue,
                    SigmoidalContrast = s.SigmoidalContrast, Clahe = s.Clahe,
                    ContrastStretch = s.ContrastStretch, Levels = s.Levels
                },
                ExposureRatios = new ExposureRatios
                {
                    Brightness = r.Brightness, Contrast = r.Contrast,
                    Shadows = r.Shadows, Highlights = r.Highlights
                },
                Parameters = new ControlParameters
                {
                    WarmthRed = p.WarmthRed, WarmthBlue = p.WarmthBlue,
                    SigmoidalMidpoint = p.SigmoidalMidpoint,
                    ClaheXTiles = p.ClaheXTiles, ClaheYTiles = p.ClaheYTiles, ClaheBins = p.ClaheBins,
                    ContrastStretchBlack = p.ContrastStretchBlack, ContrastStretchWhite = p.ContrastStretchWhite,
                    LevelBlack = p.LevelBlack, LevelWhite = p.LevelWhite
                }
            };
        }

        /// <summary>
        /// Apply this profile's values to the given settings objects.
        /// </summary>
        public void ApplyTo(CorrectionStrengths s, ExposureRatios r, ControlParameters p)
        {
            s.Brightness = Strengths.Brightness; s.Contrast = Strengths.Contrast;
            s.Shadows = Strengths.Shadows; s.Highlights = Strengths.Highlights;
            s.Saturation = Strengths.Saturation; s.ColorTemp = Strengths.ColorTemp;
            s.Red = Strengths.Red; s.Green = Strengths.Green; s.Blue = Strengths.Blue;
            s.SigmoidalContrast = Strengths.SigmoidalContrast; s.Clahe = Strengths.Clahe;
            s.ContrastStretch = Strengths.ContrastStretch; s.Levels = Strengths.Levels;

            r.Brightness = ExposureRatios.Brightness; r.Contrast = ExposureRatios.Contrast;
            r.Shadows = ExposureRatios.Shadows; r.Highlights = ExposureRatios.Highlights;

            p.WarmthRed = Parameters.WarmthRed; p.WarmthBlue = Parameters.WarmthBlue;
            p.SigmoidalMidpoint = Parameters.SigmoidalMidpoint;
            p.ClaheXTiles = Parameters.ClaheXTiles; p.ClaheYTiles = Parameters.ClaheYTiles;
            p.ClaheBins = Parameters.ClaheBins;
            p.ContrastStretchBlack = Parameters.ContrastStretchBlack;
            p.ContrastStretchWhite = Parameters.ContrastStretchWhite;
            p.LevelBlack = Parameters.LevelBlack; p.LevelWhite = Parameters.LevelWhite;
        }
    }
}
