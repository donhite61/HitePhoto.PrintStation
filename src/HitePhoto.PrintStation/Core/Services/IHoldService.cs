namespace HitePhoto.PrintStation.Core.Services;

/// <summary>
/// Only writer to orders.is_held. Toggle hold, add history note.
/// </summary>
public interface IHoldService
{
    /// <summary>Toggles hold state. Returns the new is_held value.</summary>
    bool ToggleHold(string orderId, string operatorName);
}
