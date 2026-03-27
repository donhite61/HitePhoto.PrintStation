namespace HitePhoto.PrintStation.Core.Services;

/// <summary>
/// Sends items to Noritsu via MRK writer. Periodic scan confirms print success.
/// </summary>
public interface IPrintService
{
    /// <summary>
    /// Write MRK folders for all eligible noritsu items in an order.
    /// Returns what was sent and what was skipped (no channel).
    /// Does NOT mark is_printed — that happens when Noritsu confirms.
    /// </summary>
    SendResult SendToPrinter(int orderId, HashSet<string>? sizeFilter = null);

    /// <summary>
    /// Scan Noritsu output folder for completed (e) or rejected (q) folders.
    /// Updates is_printed and adds history notes.
    /// Called periodically on a timer.
    /// </summary>
    void CheckPrintResults();
}

public record SentItem(int ItemId, string SizeLabel, int ChannelNumber, string FolderName);
public record SkippedItem(string SizeLabel, string Reason);

public record SendResult(
    List<SentItem> Sent,
    List<SkippedItem> Skipped);
