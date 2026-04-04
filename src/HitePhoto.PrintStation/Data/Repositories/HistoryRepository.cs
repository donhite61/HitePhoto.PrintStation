using Microsoft.Data.Sqlite;

namespace HitePhoto.PrintStation.Data.Repositories;

public class HistoryRepository : IHistoryRepository
{
    private readonly OrderDb _db;

    public HistoryRepository(OrderDb db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public void AddNote(string orderId, string note, string createdBy = "")
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO order_history (id, order_id, note, created_by, created_at)
            VALUES (@hid, @id, @note, @by, datetime('now','localtime'))
            """;
        cmd.Parameters.AddWithValue("@hid", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.Parameters.AddWithValue("@note", note);
        cmd.Parameters.AddWithValue("@by", createdBy);
        cmd.ExecuteNonQuery();
    }

    public void AddNoteIfNew(string orderId, string note, string createdBy = "")
    {
        using var conn = _db.OpenConnection();

        // Check if the most recent note by this author already matches
        using var check = conn.CreateCommand();
        check.CommandText = """
            SELECT note FROM order_history
            WHERE order_id = @id AND created_by = @by
            ORDER BY created_at DESC LIMIT 1
            """;
        check.Parameters.AddWithValue("@id", orderId);
        check.Parameters.AddWithValue("@by", createdBy);
        var lastNote = check.ExecuteScalar() as string;

        if (string.Equals(lastNote, note, StringComparison.Ordinal))
            return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO order_history (id, order_id, note, created_by, created_at)
            VALUES (@hid, @id, @note, @by, datetime('now','localtime'))
            """;
        cmd.Parameters.AddWithValue("@hid", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.Parameters.AddWithValue("@note", note);
        cmd.Parameters.AddWithValue("@by", createdBy);
        cmd.ExecuteNonQuery();
    }

    public List<HistoryEntry> GetNotes(string orderId)
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
                reader.GetString(0), reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3),
                reader.GetString(4)));
        }
        return results;
    }
}
