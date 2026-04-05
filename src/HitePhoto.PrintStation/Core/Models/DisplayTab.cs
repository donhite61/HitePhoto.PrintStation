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

public static class DisplayTabExtensions
{
    /// <summary>Map printed state to display_tab. Does NOT handle PendingAllStores (shared parents).</summary>
    public static int FromPrinted(bool printed) =>
        (int)(printed ? DisplayTab.Printed : DisplayTab.Pending);
}
