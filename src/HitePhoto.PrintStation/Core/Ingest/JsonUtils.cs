using System.Text.Json;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// JSON helpers for parsing Pixfizz/OHD API responses.
/// </summary>
public static class JsonUtils
{
    public static string GetStr(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        return "";
    }
}
