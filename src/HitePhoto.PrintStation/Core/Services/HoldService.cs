using HitePhoto.PrintStation.Core.Decisions;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core.Services;

public class HoldService : IHoldService
{
    private readonly IOrderRepository _orders;
    private readonly IHistoryRepository _history;
    private readonly IHoldDecision _holdDecision;

    public HoldService(IOrderRepository orders, IHistoryRepository history, IHoldDecision holdDecision)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _holdDecision = holdDecision ?? throw new ArgumentNullException(nameof(holdDecision));
    }

    public bool ToggleHold(int orderId, string operatorName)
    {
        var currentlyHeld = _holdDecision.IsHeld(orderId);
        var newState = !currentlyHeld;

        _orders.SetHold(orderId, newState);

        var action = newState ? "Held" : "Released";
        _history.AddNote(orderId, $"{action} by {operatorName}", operatorName);

        return newState;
    }
}
