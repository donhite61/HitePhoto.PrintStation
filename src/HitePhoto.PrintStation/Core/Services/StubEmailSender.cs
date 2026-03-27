using HitePhoto.PrintStation.Core.Processing;
using HitePhoto.Shared.Models;

namespace HitePhoto.PrintStation.Core.Services;

/// <summary>No-op email sender for testing. Replace with real implementation later.</summary>
public class StubEmailSender : IEmailSender
{
    public Task<EmailResult> SendOrderReadyEmailAsync(Order order, EmailTemplate? template = null)
    {
        AppLog.Info($"[STUB] Would send email to {order.CustomerEmail} for order {order.ExternalOrderId}");
        return Task.FromResult(new EmailResult { Success = true });
    }
}
