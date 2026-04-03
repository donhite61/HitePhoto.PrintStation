using System;
using System.Collections.Generic;
using HitePhoto.PrintStation.Core;

namespace HitePhoto.PrintStation.Data.Sync;

public record OutboxEntry(int Id, string TableName, string RecordId, string Operation, string PayloadJson, string CreatedAt);

public class OutboxRepository
{
    private readonly OrderDb _db;

    public OutboxRepository(OrderDb db)
    {
        _db = db;
    }

    // ── Sync outbox ─────────────────────────────────────────────────────

    public void Enqueue(string tableName, string recordId, string operation, string payloadJson)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_outbox (table_name, record_id, operation, payload_json)
            VALUES (@table, @record, @op, @payload)
            """;
        cmd.Parameters.AddWithValue("@table", tableName);
        cmd.Parameters.AddWithValue("@record", recordId);
        cmd.Parameters.AddWithValue("@op", operation);
        cmd.Parameters.AddWithValue("@payload", payloadJson);
        cmd.ExecuteNonQuery();
    }

    public List<OutboxEntry> GetPending(int limit = 50)
    {
        var entries = new List<OutboxEntry>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, table_name, record_id, operation, payload_json, created_at
            FROM sync_outbox
            WHERE pushed_at IS NULL
            ORDER BY created_at
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new OutboxEntry(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }
        return entries;
    }

    public HashSet<string> GetPendingOrderIds()
    {
        var ids = new HashSet<string>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT record_id FROM sync_outbox
            WHERE pushed_at IS NULL AND table_name IN ('orders', 'order_items', 'order_notes')
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    public void MarkPushed(int outboxId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE sync_outbox SET pushed_at = datetime('now') WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", outboxId);
        cmd.ExecuteNonQuery();
    }

    public void PurgePushed(int daysOld = 7)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM sync_outbox
            WHERE pushed_at IS NOT NULL
              AND pushed_at < datetime('now', @days)
            """;
        cmd.Parameters.AddWithValue("@days", $"-{daysOld} days");
        cmd.ExecuteNonQuery();
    }

    // ── Sync metadata ───────────────────────────────────────────────────

    public DateTime? GetLastSyncAt(string tableName, string direction)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT last_sync_at FROM sync_metadata
            WHERE table_name = @table AND direction = @dir
            """;
        cmd.Parameters.AddWithValue("@table", tableName);
        cmd.Parameters.AddWithValue("@dir", direction);
        var result = cmd.ExecuteScalar();
        if (result is string s && DateTime.TryParse(s, out var dt))
            return dt;
        return null;
    }

    public void SetLastSyncAt(string tableName, string direction, DateTime timestamp)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_metadata (table_name, direction, last_sync_at)
            VALUES (@table, @dir, @ts)
            ON CONFLICT(table_name, direction) DO UPDATE SET last_sync_at = @ts
            """;
        cmd.Parameters.AddWithValue("@table", tableName);
        cmd.Parameters.AddWithValue("@dir", direction);
        cmd.Parameters.AddWithValue("@ts", timestamp.ToString("o"));
        cmd.ExecuteNonQuery();
    }

}
