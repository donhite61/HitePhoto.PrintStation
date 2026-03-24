namespace HitePhoto.PrintStation.Core.Services;

/// <summary>
/// Notifies customers that their order is ready.
/// Dakis: always email. Pixfizz: email or Pixfizz API based on settings.
/// Sets is_notified. Adds history note.
/// </summary>
public interface INotificationService
{
    void NotifyCustomer(int orderId, string operatorName);
}
