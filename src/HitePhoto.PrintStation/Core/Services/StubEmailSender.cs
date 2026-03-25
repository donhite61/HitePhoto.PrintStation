namespace HitePhoto.PrintStation.Core.Services;

/// <summary>No-op email sender for testing. Replace with real implementation later.</summary>
public class StubEmailSender : IEmailSender
{
    public Task SendOrderReadyEmailAsync(int orderId, string customerEmail, string customerName, string templateName = "")
    {
        AppLog.Info($"[STUB] Would send email to {customerEmail} for order {orderId}");
        return Task.CompletedTask;
    }
}
