using System.IO;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Path + format-string computation shared between PixfizzApiJsonParser and
/// PixfizzFtpDownloader. Keeping them in one place ensures the parser's
/// ImageFilepath matches the downloader's destination — they're computed
/// from the same inputs by the same code.
/// </summary>
internal static class PixfizzPathHelpers
{
    private static readonly char[] InvalidPathChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>
    /// Format string for the prints/&lt;format&gt;/&lt;qty&gt; prints/ subfolder.
    /// Cut-print orders use the per-image size (e.g. "5x7"); everything else
    /// uses the API product_name (e.g. "Ceramic Mug 15oz"), sanitized for path use.
    /// </summary>
    public static string ComputeFormat(OhdJobRecord apiJob, string? manifestImageSize)
    {
        bool isNoritsu = apiJob.Process.Equals(IngestConstants.ProcessNoritsu, StringComparison.OrdinalIgnoreCase);
        if (isNoritsu && !string.IsNullOrWhiteSpace(manifestImageSize))
            return SanitizeForPath(manifestImageSize);
        return SanitizeForPath(apiJob.ProductName);
    }

    /// <summary>
    /// The final on-disk path for an image:
    ///   &lt;folderPath&gt;/prints/&lt;format&gt; format/&lt;qty&gt; prints/&lt;filename&gt;
    /// Returns "" when folderPath is empty (unpaid stub orders, no folder yet).
    /// </summary>
    public static string ComputeImagePath(string folderPath, string format, int quantity, string filename)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return "";
        return Path.Combine(folderPath, "prints", $"{format} format", $"{quantity} prints", filename);
    }

    public static string SanitizeForPath(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "unknown";
        var clean = new string(s.Where(c => !InvalidPathChars.Contains(c)).ToArray());
        return clean.Trim();
    }
}
