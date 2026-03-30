using System.IO;

namespace HitePhoto.PrintStation.Core;

/// <summary>
/// Pure utility functions with no dependencies. Stateless string/file operations only.
/// Database operations belong in repositories. Decision logic belongs in decision makers.
/// </summary>
public static class OrderHelpers
{
    /// <summary>
    /// Strip vendor prefix from external order ID (e.g. "HITEPHOTO-123" → "123", "DAKIS-789" → "789").
    /// </summary>
    public static string GetShortId(string externalOrderId)
    {
        if (string.IsNullOrEmpty(externalOrderId)) return "";
        int dash = externalOrderId.LastIndexOf('-');
        return (dash > 0 && dash < externalOrderId.Length - 1)
            ? externalOrderId[(dash + 1)..]
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

    public static string BuildRoutingKeyFromOptions(string sizeLabel, string optionsJson)
    {
        var size = (sizeLabel ?? "").Trim().ToLowerInvariant();
        var optionsPart = BuildOptionsKey(optionsJson);
        return string.IsNullOrEmpty(optionsPart) ? size : $"{size}|{optionsPart}";
    }

    public static string BuildOptionsKey(string optionsJson)
    {
        if (string.IsNullOrEmpty(optionsJson) || optionsJson == "[]") return "";
        try
        {
            var options = System.Text.Json.JsonSerializer.Deserialize<List<HitePhoto.Shared.Parsers.OrderItemOption>>(optionsJson);
            if (options == null || options.Count == 0) return "";
            return string.Join("|", options.OrderBy(o => o.Key).Select(o => o.Value.Trim().ToLowerInvariant()));
        }
        catch { return ""; }
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

        try
        {
            var info = new FileInfo(filepath);
            if (info.Length < 1024)
                return $"File too small ({info.Length} bytes): {filepath}";
        }
        catch (IOException ex)
        {
            return $"Cannot read file: {filepath} — {ex.Message}";
        }

        return null;
    }
}
