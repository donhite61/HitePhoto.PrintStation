namespace HitePhoto.PrintStation.Core;

/// <summary>
/// Order status code constants. Must match the status_codes table in the DB.
/// </summary>
public static class OrderStatusCode
{
    public const string New = "new";
    public const string InProgress = "in_progress";
    public const string OnHold = "on_hold";
    public const string Ready = "ready";
    public const string Notified = "notified";
    public const string PickedUp = "picked_up";
    public const string Cancelled = "cancelled";
    public const string SentToStore = "sent_to_store";
    public const string Shipped = "shipped";
}
