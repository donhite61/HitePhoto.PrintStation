namespace HitePhoto.PrintStation.Core.Services;

public interface IEmailSender
{
    Task SendOrderReadyEmailAsync(int orderId, string customerEmail, string customerName, string templateName = "");
}
