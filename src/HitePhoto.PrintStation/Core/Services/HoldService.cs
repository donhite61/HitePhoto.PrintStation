using HitePhoto.PrintStation.Core.Decisions;
using HitePhoto.PrintStation.Data;

namespace HitePhoto.PrintStation.Core.Services;

public class HoldService : IHoldService
{
    private readonly OrderDb _db;
    private readonly IHoldDecision _holdDecision;

    public HoldService(OrderDb db, IHoldDecision holdDecision)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _holdDecision = holdDecision ?? throw new ArgumentNullException(nameof(holdDecision));
    }

    public bool ToggleHold(int orderId, string operatorName)
    {
        var currentlyHeld = _holdDecision.IsHeld(orderId);
        var newState = !currentlyHeld;

        using var conn = _db.OpenConnection();
        using var transaction = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE orders SET is_held = @held, updated_at = datetime('now') WHERE id = @id";
            cmd.Parameters.AddWithValue("@held", newState ? 1 : 0);
            cmd.Parameters.AddWithValue("@id", orderId);
            cmd.ExecuteNonQuery();
        }

        var action = newState ? "Held" : "Released";
        OrderHelpers.AddHistoryNote(conn, orderId, $"{action} by {operatorName}", operatorName);

        transaction.Commit();
        return newState;
    }
}
