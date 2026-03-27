using HitePhoto.PrintStation.Core;
using Microsoft.Data.Sqlite;

namespace HitePhoto.PrintStation.Data.Repositories;

public class AlertRepository : IAlertRepository
{
    private readonly OrderDb _db;

    public AlertRepository(OrderDb db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public void Insert(AlertRecord alert)
    {
        using var conn = _db.OpenConnection();

        // Dedup: if an unacknowledged alert with same category+summary+order_id exists,
        // update its timestamp and details instead of inserting a duplicate row.
        using var check = conn.CreateCommand();
        check.CommandText = """
            SELECT id FROM alerts
            WHERE acknowledged = 0
              AND category = @category
              AND summary = @summary
              AND order_id IS @orderId
            LIMIT 1
            """;
        check.Parameters.AddWithValue("@category", alert.Category);
        check.Parameters.AddWithValue("@summary", alert.Summary);
        check.Parameters.AddWithValue("@orderId", (object?)alert.OrderId ?? DBNull.Value);
        var existingId = check.ExecuteScalar();

        if (existingId != null)
        {
            using var update = conn.CreateCommand();
            update.CommandText = """
                UPDATE alerts
                SET created_at = datetime('now','localtime'),
                    detail = @detail,
                    exception = @exception,
                    source_method = @method,
                    source_file = @file,
                    source_line = @line
                WHERE id = @id
                """;
            update.Parameters.AddWithValue("@id", existingId);
            update.Parameters.AddWithValue("@detail", (object?)alert.Detail ?? DBNull.Value);
            update.Parameters.AddWithValue("@exception", (object?)alert.Exception ?? DBNull.Value);
            update.Parameters.AddWithValue("@method", (object?)alert.SourceMethod ?? DBNull.Value);
            update.Parameters.AddWithValue("@file", (object?)alert.SourceFile ?? DBNull.Value);
            update.Parameters.AddWithValue("@line", (object?)alert.SourceLine ?? DBNull.Value);
            update.ExecuteNonQuery();
        }
        else
        {
            using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO alerts (severity, category, summary, order_id, detail, exception,
                                    source_method, source_file, source_line)
                VALUES (@severity, @category, @summary, @orderId, @detail, @exception,
                        @method, @file, @line)
                """;
            insert.Parameters.AddWithValue("@severity", alert.Severity);
            insert.Parameters.AddWithValue("@category", alert.Category);
            insert.Parameters.AddWithValue("@summary", alert.Summary);
            insert.Parameters.AddWithValue("@orderId", (object?)alert.OrderId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@detail", (object?)alert.Detail ?? DBNull.Value);
            insert.Parameters.AddWithValue("@exception", (object?)alert.Exception ?? DBNull.Value);
            insert.Parameters.AddWithValue("@method", (object?)alert.SourceMethod ?? DBNull.Value);
            insert.Parameters.AddWithValue("@file", (object?)alert.SourceFile ?? DBNull.Value);
            insert.Parameters.AddWithValue("@line", (object?)alert.SourceLine ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }
    }

    public List<AlertRecord> GetRecent(int days)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, severity, category, summary, order_id, detail, exception,
                   source_method, source_file, source_line, created_at, acknowledged
            FROM alerts
            WHERE created_at >= datetime('now', @cutoff, 'localtime')
            ORDER BY created_at DESC
            """;
        cmd.Parameters.AddWithValue("@cutoff", $"-{days} days");
        return ReadAlerts(cmd);
    }

    public List<AlertRecord> GetUnacknowledged(int limit = 50)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, severity, category, summary, order_id, detail, exception,
                   source_method, source_file, source_line, created_at, acknowledged
            FROM alerts
            WHERE acknowledged = 0
            ORDER BY created_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        return ReadAlerts(cmd);
    }

    public int CountUnacknowledged()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM alerts WHERE acknowledged = 0";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Acknowledge(int alertId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE alerts SET acknowledged = 1 WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", alertId);
        cmd.ExecuteNonQuery();
    }

    public void AcknowledgeAll()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE alerts SET acknowledged = 1 WHERE acknowledged = 0";
        cmd.ExecuteNonQuery();
    }

    public void PurgeOlderThan(int days)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM alerts WHERE created_at < datetime('now', @cutoff, 'localtime')";
        cmd.Parameters.AddWithValue("@cutoff", $"-{days} days");
        var deleted = cmd.ExecuteNonQuery();
        if (deleted > 0)
            AppLog.Info($"Purged {deleted} alerts older than {days} days");
    }

    private static List<AlertRecord> ReadAlerts(SqliteCommand cmd)
    {
        var results = new List<AlertRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new AlertRecord(
                Id: reader.GetInt32(0),
                Severity: reader.GetString(1),
                Category: reader.GetString(2),
                Summary: reader.GetString(3),
                OrderId: reader.IsDBNull(4) ? null : reader.GetString(4),
                Detail: reader.IsDBNull(5) ? null : reader.GetString(5),
                Exception: reader.IsDBNull(6) ? null : reader.GetString(6),
                SourceMethod: reader.IsDBNull(7) ? null : reader.GetString(7),
                SourceFile: reader.IsDBNull(8) ? null : reader.GetString(8),
                SourceLine: reader.IsDBNull(9) ? null : reader.GetInt32(9),
                CreatedAt: reader.GetString(10),
                Acknowledged: reader.GetInt32(11) != 0));
        }
        return results;
    }
}
