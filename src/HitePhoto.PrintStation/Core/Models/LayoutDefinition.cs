namespace HitePhoto.PrintStation.Core.Models;

/// <summary>
/// Defines a print layout — how one or more copies of an image are arranged
/// on a target paper size with padding, gaps, and margins.
/// All measurements are in inches.
/// </summary>
public class LayoutDefinition
{
    public string Name { get; set; } = string.Empty;

    // ── Individual print size (source image dimensions in inches) ───────
    public double PrintWidth  { get; set; }
    public double PrintHeight { get; set; }

    // ── Grid arrangement ────────────────────────────────────────────────
    public int Rows    { get; set; } = 1;
    public int Columns { get; set; } = 1;

    // ── Spacing (all in inches) ─────────────────────────────────────────
    public double GapHorizontal { get; set; }
    public double GapVertical   { get; set; }
    public double OffsetBefore  { get; set; }

    public double MarginLeft   { get; set; }
    public double MarginRight  { get; set; }
    public double MarginTop    { get; set; }
    public double MarginBottom { get; set; }

    // ── Target output ───────────────────────────────────────────────────
    public int    TargetChannelNumber { get; set; }
    public string TargetSizeLabel     { get; set; } = string.Empty;

    // ── Auto-routing ────────────────────────────────────────────────────
    public List<string> AutoMatchSizes { get; set; } = new();

    // ── Computed ────────────────────────────────────────────────────────
    public double CalculatedPageWidth =>
        MarginLeft + (Columns * PrintWidth) +
        Math.Max(0, Columns - 1) * GapHorizontal + MarginRight;

    public double CalculatedPageHeight =>
        MarginTop + OffsetBefore + (Rows * PrintHeight) +
        Math.Max(0, Rows - 1) * GapVertical + MarginBottom;

    public int CopiesPerSheet => Rows * Columns;

    public string PrintSizeDisplay => $"{PrintWidth} x {PrintHeight}";
    public string GridDisplay => $"{Rows} x {Columns}";
}
