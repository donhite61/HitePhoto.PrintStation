using System.IO;
using Microsoft.Data.Sqlite;

namespace HitePhoto.PrintStation.Core;

/// <summary>
/// Shared functions used by multiple services and decision makers.
/// Each function lives here once — no copies in individual services.
/// </summary>
public static class OrderHelpers
{
    /// <summary>
    /// Add a note to order_history. Called by every service that changes order state.
    /// </summary>
    public static void AddHistoryNote(SqliteConnection conn, int orderId, string note, string createdBy = "")
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO order_history (order_id, note, created_by, created_at)
            VALUES (@id, @note, @by, datetime('now'))
            """;
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.Parameters.AddWithValue("@note", note);
        cmd.Parameters.AddWithValue("@by", createdBy);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Get a store's short name by ID.
    /// </summary>
    public static string GetStoreName(SqliteConnection conn, int storeId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT short_name FROM stores WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", storeId);
        return (string?)cmd.ExecuteScalar() ?? $"store {storeId}";
    }

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
