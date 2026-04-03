using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core.Decisions;

public class HoldDecision : IHoldDecision
{
    private readonly IOrderRepository _orders;

    public HoldDecision(IOrderRepository orders)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
    }

    public bool IsHeld(string orderId)
    {
        var order = _orders.GetOrder(orderId);
        return order?.IsHeld ?? false;
    }
}
