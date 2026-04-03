namespace HitePhoto.PrintStation.Core.Decisions;

/// <summary>
/// Single authority for whether an order is held.
/// Reads orders.is_held from SQLite. Nothing else checks hold state.
/// </summary>
public interface IHoldDecision
{
    bool IsHeld(string orderId);
}
