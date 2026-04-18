using HitePhoto.PrintStation.Core.Processing;

namespace HitePhoto.PrintStation.Core.Services;

/// <summary>
/// Notifies customers that their order is ready.
/// Dakis: always email. Pixfizz: email or Pixfizz API based on settings.
/// Sets notified_at. Adds history note.
/// </summary>
public interface INotificationService
{
    Task NotifyCustomerAsync(string orderId, string operatorName, EmailTemplate? templateOverride = null);
}
