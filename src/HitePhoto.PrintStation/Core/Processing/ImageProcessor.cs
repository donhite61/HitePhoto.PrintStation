using System;
using System.IO;
using ImageMagick;
using HitePhoto.PrintStation.Core.Models;

namespace HitePhoto.PrintStation.Core.Processing
{
    /// <summary>
    /// Image correction engine using Magick.NET.
    ///
    /// Pipeline:
    ///   Auto-corrections (toggles): AutoLevel, AutoGamma, WhiteBalance, Normalize
    ///   Brightness → Contrast → Shadows → Highlights → Saturation → ColorTemp
    ///   → R/G/B → SigmoidalContrast → CLAHE → ContrastStretch → Levels
    ///   → Conversions: Grayscale, Sepia
    ///   → Write JPEG Q95
    ///
    /// Strength multipliers scale the impact of each correction type.
    /// </summary>
    public static class ImageProcessor
    {
        // ── Public API ────────────────────────────────────────────────────────

        public static string ApplyCorrections(
            string sourcePath,
            string outputPath,
            int exposure,
            int brightness,
            int contrast,
            int shadows,
            int highlights,
            int saturation,
            int colorTemp,
            int red,
            int green,
            int blue,
            CorrectionStrengths? strengths = null,
            ImageCorrectionState? state = null,
            ControlParameters? parameters = null)
        {
            strengths  ??= new CorrectionStrengths();
            parameters ??= new ControlParameters();

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            using var image = new MagickImage(sourcePath);
            image.AutoOrient();

            ApplyCorrectionPipeline(image, exposure, brightness, contrast, shadows, highlights,
                saturation, colorTemp, red, green, blue, strengths, state, parameters);

            image.Quality = 95;
            image.Write(outputPath, MagickFormat.Jpeg);

            return outputPath;
        }

        public static string BuildCorrectedPath(string originalImagePath, string orderFolderPath)
        {
            string fileName = Path.GetFileNameWithoutExtension(originalImagePath) + "_cc.jpg";
            return Path.Combine(orderFolderPath, "color_corrected", fileName);
        }

        /// <summary>
        /// Apply corrections to an in-memory MagickImage (used for preview).
        /// </summary>
        public static void ApplyCorrectionPipeline(
            MagickImage image,
            int exposure,
            int brightness, int contrast, int shadows, int highlights,
            int saturation, int colorTemp,
            int red, int green, int blue,
            CorrectionStrengths strengths,
            ImageCorrectionState? state = null,
            ControlParameters? parameters = null)
        {
            parameters ??= new ControlParameters();

            // ── Auto-corrections (toggle-based) ──────────────────────────────
            if (state?.AutoLevel == true)
                image.AutoLevel();
            if (state?.AutoGamma == true)
                image.AutoGamma();
            if (state?.WhiteBalance == true)
                ApplyWhiteBalance(image);
            if (state?.Normalize == true)
                image.Normalize();

            // ── LUT-based corrections (non-compounding) ────────────────────
            // All basic corrections are computed into per-channel lookup tables
            // and applied in a single pass, matching IccTestApp's approach.
            bool hasBasic = exposure != 0 || brightness != 0 || contrast != 0 ||
                            shadows != 0 || highlights != 0 || colorTemp != 0 ||
                            red != 0 || green != 0 || blue != 0;

            if (hasBasic)
            {
                var rLut = new double[256];
                var gLut = new double[256];
                var bLut = new double[256];

                for (int i = 0; i < 256; i++)
                {
                    double v = i / 255.0;

                    // Exposure (gamma/EV-style: lifts midtones & shadows, preserves highlights)
                    if (exposure != 0)
                    {
                        double ev = 1.0 + exposure * 0.08;
                        v = Math.Pow(v, 1.0 / ev);
                    }

                    // Brightness (linear lift/pull)
                    if (brightness != 0)
                        v += brightness * strengths.Brightness * 0.04;

                    // Contrast (S-curve around midpoint)
                    if (contrast != 0)
                    {
                        double factor = 1.0 + contrast * strengths.Contrast * 0.08;
                        v = (v - 0.5) * factor + 0.5;
                    }

                    // Shadows (lift dark areas, leave highlights alone)
                    if (shadows != 0)
                    {
                        double t = 1.0 - v;
                        t = t * t;
                        v += shadows * strengths.Shadows * 0.04 * t;
                    }

                    // Highlights (push bright areas, leave shadows alone)
                    if (highlights != 0)
                    {
                        double t = v;
                        t = t * t;
                        v += highlights * strengths.Highlights * 0.04 * t;
                    }

                    rLut[i] = gLut[i] = bLut[i] = Math.Clamp(v, 0.0, 1.0);
                }

                // Color temperature (warm/cool shift on R and B channels)
                if (colorTemp != 0)
                {
                    double warmShift = colorTemp * strengths.ColorTemp * parameters.WarmthRed * 0.015;
                    for (int i = 0; i < 256; i++)
                    {
                        rLut[i] = Math.Clamp(rLut[i] + warmShift, 0.0, 1.0);
                        bLut[i] = Math.Clamp(bLut[i] - warmShift * parameters.WarmthBlue, 0.0, 1.0);
                    }
                }

                // Per-channel RGB offsets (CMY labels: +Cyan = -Red)
                if (red != 0)
                    for (int i = 0; i < 256; i++)
                        rLut[i] = Math.Clamp(rLut[i] - red * strengths.Red * 0.025, 0.0, 1.0);
                if (green != 0)
                    for (int i = 0; i < 256; i++)
                        gLut[i] = Math.Clamp(gLut[i] - green * strengths.Green * 0.025, 0.0, 1.0);
                if (blue != 0)
                    for (int i = 0; i < 256; i++)
                        bLut[i] = Math.Clamp(bLut[i] - blue * strengths.Blue * 0.025, 0.0, 1.0);

                // Apply all LUTs via a single RGB CLUT image in one pass
                const double Q16Max = 65535.0;
                using var clut = new MagickImage(MagickColors.Black, 256, 1);
                using var pixels = clut.GetPixels();
                for (int i = 0; i < 256; i++)
                {
                    ushort r = (ushort)(rLut[i] * Q16Max);
                    ushort g = (ushort)(gLut[i] * Q16Max);
                    ushort b = (ushort)(bLut[i] * Q16Max);
                    pixels.SetPixel(i, 0, new[] { r, g, b });
                }
                image.Clut(clut, PixelInterpolateMethod.Bilinear);
            }

            // ── Saturation ───────────────────────────────────────────────────
            if (saturation != 0)
            {
                double satPct = 100.0 + saturation * strengths.Saturation * 7.0;
                image.Modulate(new Percentage(100), new Percentage(satPct), new Percentage(100));
            }

            // ── Advanced corrections ─────────────────────────────────────────
            int sc = state?.SigmoidalContrast ?? 0;
            int cl = state?.Clahe ?? 0;
            int cs = state?.ContrastStretch ?? 0;
            int lv = state?.Levels ?? 0;

            if (sc != 0)
            {
                // Magick.NET SigmoidalContrast needs values ~3-20 for visible effect.
                // Map -10..+10 to factor range: value 1 → ~1.5, value 10 → ~15
                double scFactor = Math.Abs(sc) * strengths.SigmoidalContrast * 1.5;
                if (sc > 0)
                    image.SigmoidalContrast(scFactor, new Percentage(parameters.SigmoidalMidpoint));
                else
                    image.InverseSigmoidalContrast(scFactor, new Percentage(parameters.SigmoidalMidpoint));
            }

            if (cl != 0)
            {
                double clFactor = Math.Abs(cl * strengths.Clahe);
                if (clFactor > 0)
                {
                    image.Clahe(
                        (uint)parameters.ClaheXTiles,
                        (uint)parameters.ClaheYTiles,
                        (uint)parameters.ClaheBins,
                        clFactor);
                }
            }

            if (cs > 0)
            {
                // Positive: clip extremes and stretch histogram (increase contrast)
                double blackClip = parameters.ContrastStretchBlack * cs * strengths.ContrastStretch;
                double whiteClip = parameters.ContrastStretchWhite * cs * strengths.ContrastStretch;
                image.ContrastStretch(new Percentage(blackClip), new Percentage(whiteClip));
            }
            else if (cs < 0)
            {
                // Negative: soften contrast via inverse sigmoidal curve
                double factor = Math.Abs(cs) * strengths.ContrastStretch * 1.0;
                image.InverseSigmoidalContrast(factor, new Percentage(50));
            }

            if (lv != 0)
            {
                double shift = lv * strengths.Levels * 2;
                double bp = Math.Clamp(parameters.LevelBlack + shift, 0, 99);
                double wp = Math.Clamp(parameters.LevelWhite - Math.Abs(shift), bp + 1, 100);
                image.Level(new Percentage(bp), new Percentage(wp));
            }

            // ── Conversions (toggles) ────────────────────────────────────────
            if (state?.Grayscale == true)
                image.Grayscale();
            if (state?.Sepia == true)
                image.SepiaTone(new Percentage(80));
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void ApplyWhiteBalance(MagickImage image)
        {
            // Simple white balance: auto-level each channel independently
            image.AutoLevel(Channels.Red);
            image.AutoLevel(Channels.Green);
            image.AutoLevel(Channels.Blue);
        }
    }
}
