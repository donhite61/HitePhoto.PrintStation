namespace HitePhoto.PrintStation.Core.Services;

/// <summary>
/// Single authority for verify-and-repair: compare source files on disk
/// against SQLite, insert missing, repair mismatched, flag missing files.
/// TXT is source of truth for Pixfizz; order.yml for Dakis.
/// Never overwrites history, hold state, or printed state.
/// </summary>
public interface IOrderVerifier
{
    /// <summary>
    /// Verify/repair recent orders by scanning disk and DB.
    /// Called at startup and on timer.
    /// </summary>
    VerifyResult VerifyRecentOrders(int days);

    /// <summary>
    /// Verify/repair from pre-built lists (disk folders vs DB records).
    /// Both lists should be empty at the end — matched entries are removed.
    /// </summary>
    VerifyResult VerifyOrders(
        Dictionary<string, (string Path, string Source)> folderList,
        Dictionary<string, (string Id, string FolderPath, string SourceCode)> dbList);

    /// <summary>
    /// Verify/repair a single order. Called on operator click, ingest re-encounter, print-time.
    /// </summary>
    VerifyResult VerifyOrder(string externalOrderId, string folderPath,
        string sourceCode, string dbOrderId);

    /// <summary>
    /// Full repair for a single order: compare source file to DB, fix mismatches, update file statuses.
    /// Called on click (Printed tab) and periodically (Pending tab).
    /// </summary>
    int RepairOrder(string dbOrderId, string folderPath, string sourceCode);
}

public record VerifyResult(int Matched, int Inserted, int Repaired, int Errors);
