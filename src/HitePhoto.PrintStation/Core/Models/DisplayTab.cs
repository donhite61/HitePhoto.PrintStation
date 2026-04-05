namespace HitePhoto.PrintStation.Core.Models;

/// <summary>
/// Which tab an order appears on. Stored as INTEGER in SQLite/MariaDB.
/// </summary>
public enum DisplayTab
{
    Pending = 1,
    Printed = 2,
    PendingAllStores = 3
}
