using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core.Services;

/// <summary>
/// Writes print jobs to a printer. NoritsuMrkWriter implements this.
/// </summary>
public interface IPrinterWriter
{
    /// <summary>
    /// Write a print job for a size group. Returns the output folder name.
    /// </summary>
    string WritePrintJob(string externalOrderId, string sizeLabel, int channelNumber,
        List<OrderItemRecord> items, Action<int, int>? onProgress = null);
}
