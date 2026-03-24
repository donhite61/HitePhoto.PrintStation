using Microsoft.Data.Sqlite;

namespace HitePhoto.PrintStation.Data.Repositories;

public class HistoryRepository : IHistoryRepository
{
    private readonly OrderDb _db;

    public HistoryRepository(OrderDb db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public void AddNote(int orderId, string note, string createdBy = "")
    {
        using var conn = _db.OpenConnection();
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

    public List<HistoryEntry> GetNotes(int orderId)
    {
        var results = new List<HistoryEntry>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, order_id, note, created_by, created_at
            FROM order_history WHERE order_id = @id ORDER BY created_at DESC
            """;
        cmd.Parameters.AddWithValue("@id", orderId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new HistoryEntry(
                reader.GetInt32(0), reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3),
                reader.GetString(4)));
        }
        return results;
    }
}
