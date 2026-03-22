using System.IO;
using ImageMagick;
using HitePhoto.PrintStation.Core.Models;

namespace HitePhoto.PrintStation.Core.Processing;

/// <summary>
/// Renders images onto a white canvas according to a LayoutDefinition using Magick.NET.
/// Output dimensions match the layout's calculated page size at 300 DPI.
/// </summary>
public static class LayoutProcessor
{
    private const int DefaultDpi = 300;
    private const int JpegQuality = 95;

    /// <summary>
    /// Applies the layout to a source image, producing a new image file
    /// on a white canvas at the target page size.
    /// </summary>
    public static string ApplyLayout(
        string sourceImagePath,
        string outputPath,
        LayoutDefinition layout)
    {
        AppLog.Info($"LayoutProcessor: {Path.GetFileName(sourceImagePath)} → {layout.Name} ({layout.Rows}x{layout.Columns})");

        using var source = new MagickImage(sourceImagePath);

        int dpi = DefaultDpi;
        int canvasW = (int)Math.Round(layout.CalculatedPageWidth * dpi);
        int canvasH = (int)Math.Round(layout.CalculatedPageHeight * dpi);

        if (canvasW <= 0 || canvasH <= 0)
            throw new InvalidOperationException(
                $"Invalid layout canvas size: {canvasW}x{canvasH}px " +
                $"({layout.CalculatedPageWidth:F3}x{layout.CalculatedPageHeight:F3} in)");

        using var canvas = new MagickImage(MagickColors.White, (uint)canvasW, (uint)canvasH);
        canvas.Density = new Density(dpi, dpi);

        int cellW = (int)Math.Round(layout.PrintWidth * dpi);
        int cellH = (int)Math.Round(layout.PrintHeight * dpi);

        // Auto-rotate source to match cell orientation
        bool imageIsLandscape = source.Width > source.Height;
        bool cellIsLandscape = cellW > cellH;
        if (imageIsLandscape != cellIsLandscape)
            source.Rotate(90);

        using var resized = source.Clone() as MagickImage;
        resized!.Resize((uint)cellW, (uint)cellH);

        for (int row = 0; row < layout.Rows; row++)
        {
            for (int col = 0; col < layout.Columns; col++)
            {
                int x = (int)Math.Round((layout.MarginLeft + col * (layout.PrintWidth + layout.GapHorizontal)) * dpi);
                int y = (int)Math.Round((layout.MarginTop + layout.OffsetBefore + row * (layout.PrintHeight + layout.GapVertical)) * dpi);

                canvas.Composite(resized, x, y, CompositeOperator.Over);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        canvas.Quality = JpegQuality;
        canvas.Write(outputPath, MagickFormat.Jpeg);

        return outputPath;
    }

    public static string BuildLayoutPath(
        string originalImagePath,
        string orderFolderPath,
        string layoutName)
    {
        string safeName = SanitizeFileName(layoutName);
        string fileName = Path.GetFileNameWithoutExtension(originalImagePath)
                          + $"_lay_{safeName}.jpg";
        return Path.Combine(orderFolderPath, "layout_processed", fileName);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
