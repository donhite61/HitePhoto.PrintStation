using System.IO;
using ImageMagick;

namespace HitePhoto.PrintStation.Core.Processing;

/// <summary>
/// Prepares images for the Noritsu printer:
/// sRGB color profile conversion, EXIF auto-orient, metadata strip.
/// </summary>
public static class ImagePreparer
{
    /// <summary>
    /// Prepares an image for the Noritsu:
    /// 1. AutoOrient — fix EXIF rotation
    /// 2. Convert to sRGB color profile (if not already)
    /// 3. Strip metadata (EXIF/IPTC/XMP)
    /// 4. Write JPEG at quality 95
    /// </summary>
    private static readonly HashSet<string> SrgbDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "sRGB", "sRGB IEC61966-2.1", "sRGB IEC61966-2-1", "sRGB built-in"
    };

    public static void PrepareForPrint(string sourcePath, string destPath)
    {
        using var image = new MagickImage(sourcePath);

        image.AutoOrient();

        var profile = image.GetColorProfile();
        if (profile != null)
        {
            // Skip expensive transform if already sRGB
            bool alreadySrgb = profile.Description != null
                && SrgbDescriptions.Contains(profile.Description);
            if (!alreadySrgb)
                image.TransformColorSpace(ColorProfiles.SRGB);
        }
        else
        {
            image.ColorSpace = ColorSpace.sRGB;
        }

        image.Strip();
        image.Quality = 95;
        image.Write(destPath);
    }
}
