using HitePhoto.PrintStation.Core.Models;
using HitePhoto.PrintStation.Core.Processing;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core.Services;

public class NotificationService : INotificationService
{
    private readonly IOrderRepository _orders;
    private readonly IHistoryRepository _history;
    private readonly IEmailSender _emailSender;
    private readonly IPixfizzNotifier _pixfizzNotifier;
    private readonly AppSettings _settings;

    public NotificationService(
        IOrderRepository orders,
        IHistoryRepository history,
        IEmailSender emailSender,
        IPixfizzNotifier pixfizzNotifier,
        AppSettings settings)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _emailSender = emailSender ?? throw new ArgumentNullException(nameof(emailSender));
        _pixfizzNotifier = pixfizzNotifier ?? throw new ArgumentNullException(nameof(pixfizzNotifier));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public void NotifyCustomer(string orderId, string operatorName, EmailTemplate? templateOverride = null)
    {
        var order = _orders.GetOrder(orderId);
        if (order is null)
            throw new InvalidOperationException($"Order {orderId} not found.");

        var usePixfizz = order.Source == OrderSource.Pixfizz
            && _settings.PixfizzNotifyMode.Equals("Pixfizz", StringComparison.OrdinalIgnoreCase);

        string method;
        if (usePixfizz)
        {
            // TODO: need pixfizz job_id to call API
            method = "Pixfizz";
        }
        else
        {
            if (string.IsNullOrEmpty(order.CustomerEmail))
            {
                AlertCollector.Error(AlertCategory.Network,
                    $"Cannot notify — no email address for order {order.ExternalOrderId}",
                    orderId: order.ExternalOrderId);
                return;
            }

            var fullOrder = _orders.GetFullOrder(orderId);
            if (fullOrder is null)
            {
                AlertCollector.Error(AlertCategory.Database,
                    $"Cannot load full order {orderId} for email",
                    orderId: order.ExternalOrderId,
                    detail: $"Attempted: load full order for email template. " +
                            $"Expected: order with customer details. " +
                            $"Found: null from GetFullOrder. " +
                            $"Context: sending notification email. " +
                            $"State: order {order.ExternalOrderId}, dbId={orderId}.");
                return;
            }

            var template = templateOverride
                ?? _settings.GetDefaultTemplate(fullOrder.DeliveryMethodId == DeliveryMethodId.Ship);
            var result = _emailSender.SendOrderReadyEmailAsync(fullOrder, template).GetAwaiter().GetResult();
            if (!result.Success)
            {
                AlertCollector.Error(AlertCategory.Network,
                    $"Email failed for {order.ExternalOrderId}: {result.ErrorMessage}",
                    orderId: order.ExternalOrderId);
                return;
            }

            method = "Email";
        }

        _orders.SetNotified(orderId);
        _history.AddNote(orderId, $"Customer notified via {method}", operatorName);
    }
}
