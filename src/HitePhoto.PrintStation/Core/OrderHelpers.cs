using System.IO;

namespace HitePhoto.PrintStation.Core;

/// <summary>
/// Pure utility functions with no dependencies. Stateless string/file operations only.
/// Database operations belong in repositories. Decision logic belongs in decision makers.
/// </summary>
public static class OrderHelpers
{
    /// <summary>
    /// Build a deterministic routing key from size and media.
    /// Used by IChannelDecision and channel mapping UI.
    /// </summary>
    /// <summary>
    /// Build routing key from size + options key (already extracted from JSON).
    /// Format: "4x6" (no options) or "4x6|white border" (with options).
    /// This is the ONE routing key format — used by save, lookup, and display.
    /// </summary>
    public static string BuildRoutingKey(string sizeLabel, string optionsKey)
    {
        var size = (sizeLabel ?? "").Trim().ToLowerInvariant();
        var options = (optionsKey ?? "").Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(options) ? size : $"{size}|{options}";
    }


    /// <summary>
    /// Build display-friendly options string, filtering out defaults.
    /// Returns empty string if all options are defaults.
    /// </summary>
    public static string BuildDisplayOptions(string optionsJson, HashSet<(string Key, string Value)> defaults)
    {
        if (string.IsNullOrEmpty(optionsJson) || optionsJson == "[]") return "";
        try
        {
            var options = System.Text.Json.JsonSerializer.Deserialize<List<HitePhoto.Shared.Parsers.OrderItemOption>>(optionsJson);
            if (options == null || options.Count == 0) return "";
            var nonDefaults = options
                .Where(o => !string.IsNullOrEmpty(o.Value))
                .Where(o => !defaults.Contains((o.Key ?? "", o.Value.Trim())))
                .Select(o => o.Value.Trim())
                .ToList();
            return string.Join(", ", nonDefaults);
        }
        catch { return ""; }
    }

    public static string BuildOptionsKey(string optionsJson)
    {
        if (string.IsNullOrEmpty(optionsJson) || optionsJson == "[]") return "";
        try
        {
            var options = System.Text.Json.JsonSerializer.Deserialize<List<HitePhoto.Shared.Parsers.OrderItemOption>>(optionsJson);
            if (options == null || options.Count == 0) return "";
            return string.Join("|", options.Where(o => !string.IsNullOrEmpty(o.Value)).OrderBy(o => o.Key ?? "").Select(o => o.Value.Trim().ToLowerInvariant()));
        }
        catch { return ""; }
    }

    /// <summary>
    /// Verify a single image file: exists, >1KB, JPEG magic bytes.
    /// Returns null if valid, error message if not.
    /// Called at ingest and on order click.
    /// </summary>
    public static string FormatPhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length == 10)
            return $"({digits[..3]}) {digits[3..6]}-{digits[6..]}";
        if (digits.Length == 11 && digits[0] == '1')
            return $"({digits[1..4]}) {digits[4..7]}-{digits[7..]}";
        return raw;
    }

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
