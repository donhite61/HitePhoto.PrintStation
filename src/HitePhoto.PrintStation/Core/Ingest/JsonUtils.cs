using System.Text.Json;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// JSON helpers for parsing Pixfizz/OHD API responses.
/// Tolerant of missing/wrong-type fields — returns the default rather than throwing,
/// so an upstream schema drift can't take the whole ingest pipeline down.
/// </summary>
public static class JsonUtils
{
    public static string GetStr(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return "";
    }

    public static int GetInt(JsonElement el, string prop, int defaultValue = 0)
    {
        if (!el.TryGetProperty(prop, out var v)) return defaultValue;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return defaultValue;
    }

    public static bool GetBool(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return false;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        if (v.ValueKind == JsonValueKind.String &&
            bool.TryParse(v.GetString(), out var b)) return b;
        return false;
    }

    public static DateTime? GetDate(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        if (string.IsNullOrEmpty(s)) return null;
        if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt;
        return null;
    }
}
