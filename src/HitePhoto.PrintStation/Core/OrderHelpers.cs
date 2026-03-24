using System.IO;

namespace HitePhoto.PrintStation.Core;

/// <summary>
/// Pure utility functions with no dependencies. Stateless string/file operations only.
/// Database operations belong in repositories. Decision logic belongs in decision makers.
/// </summary>
public static class OrderHelpers
{
    /// <summary>
    /// Strip "HITEPHOTO-" prefix from external order ID for folder naming.
    /// </summary>
    public static string GetShortId(string externalOrderId)
    {
        const string prefix = "HITEPHOTO-";
        return externalOrderId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? externalOrderId.Substring(prefix.Length)
            : externalOrderId;
    }

    /// <summary>
    /// Build a deterministic routing key from size and media.
    /// Used by IChannelDecision and channel mapping UI.
    /// </summary>
    public static string BuildRoutingKey(string sizeLabel, string mediaType)
    {
        var size = (sizeLabel ?? "").Trim().ToLowerInvariant();
        var media = (mediaType ?? "").Trim().ToLowerInvariant();
        return $"{size}|{media}";
    }

    /// <summary>
    /// Verify a single image file: exists, >1KB, JPEG magic bytes.
    /// Returns null if valid, error message if not.
    /// Called at ingest and on order click.
    /// </summary>
    public static string? VerifyFile(string filepath)
    {
        if (string.IsNullOrEmpty(filepath))
            return "No file path specified.";

        if (!File.Exists(filepath))
            return $"File not found: {filepath}";

        var info = new FileInfo(filepath);
        if (info.Length < 1024)
            return $"File too small ({info.Length} bytes): {filepath}";

        try
        {
            using var stream = File.OpenRead(filepath);
            var header = new byte[2];
            if (stream.Read(header, 0, 2) < 2)
                return $"Cannot read file header: {filepath}";

            if (header[0] != 0xFF || header[1] != 0xD8)
                return $"Not a valid JPEG (bad magic bytes): {filepath}";
        }
        catch (IOException ex)
        {
            return $"Cannot read file: {filepath} — {ex.Message}";
        }

        return null;
    }
}
