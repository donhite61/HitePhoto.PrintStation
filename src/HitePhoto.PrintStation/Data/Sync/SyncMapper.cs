using System;
using System.Collections.Generic;

namespace HitePhoto.PrintStation.Data.Sync;

/// <summary>
/// Maps fields between SQLite (PrintStation) and MariaDB (LabApi) schemas.
/// Both databases use the same IDs for statuses (1-9) and sources (1=pixfizz, 2=dakis).
/// MariaDB has additional sources (3=walkin, 4=phone, 5=inter_store_transfer).
/// SQLite has source 3=dashboard which doesn't exist in MariaDB seed data.
/// </summary>
public static class SyncMapper
{
    // MariaDB order_sources seed: 1=pixfizz, 2=dakis, 3=walkin, 4=phone, 5=inter_store_transfer
    // SQLite order_sources seed:  1=pixfizz, 2=dakis, 3=dashboard
    // Status IDs are identical in both (1-9).

    private static readonly Dictionary<string, int> SourceCodeToId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pixfizz"] = 1,
        ["dakis"] = 2,
        ["walkin"] = 3,
        ["phone"] = 4,
        ["inter_store_transfer"] = 5,
        ["dashboard"] = 3, // dashboard maps to walkin in MariaDB (manual entry)
    };

    private static readonly Dictionary<int, string> MariaDbSourceIdToCode = new()
    {
        [1] = "pixfizz",
        [2] = "dakis",
        [3] = "walkin",
        [4] = "phone",
        [5] = "inter_store_transfer",
    };

    private static readonly Dictionary<string, int> StatusCodeToId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["new"] = 1,
        ["in_progress"] = 2,
        ["on_hold"] = 3,
        ["ready"] = 4,
        ["notified"] = 5,
        ["picked_up"] = 6,
        ["cancelled"] = 7,
        ["sent_to_store"] = 8,
        ["shipped"] = 9,
    };

    private static readonly Dictionary<int, string> StatusIdToCode = new()
    {
        [1] = "new",
        [2] = "in_progress",
        [3] = "on_hold",
        [4] = "ready",
        [5] = "notified",
        [6] = "picked_up",
        [7] = "cancelled",
        [8] = "sent_to_store",
        [9] = "shipped",
    };

    /// <summary>Convert a source_code string to MariaDB order_source_id.</summary>
    public static int SourceCodeToSourceId(string sourceCode)
    {
        if (SourceCodeToId.TryGetValue(sourceCode, out var id))
            return id;
        return 1; // default to pixfizz if unknown
    }

    /// <summary>Convert a MariaDB order_source_id to source_code string.</summary>
    public static string SourceIdToSourceCode(int sourceId)
    {
        if (MariaDbSourceIdToCode.TryGetValue(sourceId, out var code))
            return code;
        return "pixfizz";
    }

    /// <summary>Convert a status_code string to order_status_id.</summary>
    public static int StatusCodeToStatusId(string statusCode)
    {
        if (StatusCodeToId.TryGetValue(statusCode, out var id))
            return id;
        return 1;
    }

    /// <summary>Convert an order_status_id to status_code string.</summary>
    public static string StatusIdToStatusCode(int statusId)
    {
        if (StatusIdToCode.TryGetValue(statusId, out var code))
            return code;
        return "new";
    }
}
