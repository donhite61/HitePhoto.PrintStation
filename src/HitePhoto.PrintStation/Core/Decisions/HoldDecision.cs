using Microsoft.Data.Sqlite;
using HitePhoto.PrintStation.Data;

namespace HitePhoto.PrintStation.Core.Decisions;

public class HoldDecision : IHoldDecision
{
    private readonly OrderDb _db;

    public HoldDecision(OrderDb db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public bool IsHeld(int orderId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_held FROM orders WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", orderId);

        var result = cmd.ExecuteScalar();
        return result is not null && Convert.ToInt32(result) == 1;
    }
}
