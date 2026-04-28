using HitePhoto.PrintStation.Core.Services;
using HitePhoto.PrintStation.Data.Repositories;
using HitePhoto.Shared.Models;

namespace HitePhoto.PrintStation.Core.Processing;

/// <summary>
/// Bridges IPrinterWriter (repository records) to NoritsuMrkWriter (shared Order model).
/// </summary>
public class NoritsuMrkWriterAdapter : IPrinterWriter
{
    private readonly NoritsuMrkWriter _writer;

    public NoritsuMrkWriterAdapter(NoritsuMrkWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public string WritePrintJob(string externalOrderId, string sizeLabel, int channelNumber,
        List<OrderItemRecord> items, Action<int, int>? onProgress = null)
    {
        var order = new Order { ExternalOrderId = externalOrderId };

        var orderItems = items.Select(r => new OrderItem
        {
            Id = r.Id,
            ImageFilepath = r.ImageFilepath,
            ImageFilename = r.ImageFilename,
            Quantity = r.Quantity,
            SizeLabel = r.SizeLabel,
            MediaType = r.MediaType
        }).ToList();

        return _writer.WriteMrk(order, sizeLabel, channelNumber, orderItems, onProgress);
    }
}
