using System.IO;
using HitePhoto.PrintStation.Core.Decisions;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core.Services;

public class PrintService : IPrintService
{
    private readonly IOrderRepository _orders;
    private readonly IHistoryRepository _history;
    private readonly IChannelDecision _channel;
    private readonly string _noritsuOutputRoot;

    // TODO: confirm these codes from Noritsu
    private const char SuccessPrefix = 'e';
    private const char ErrorPrefix = 'q';

    public PrintService(
        IOrderRepository orders,
        IHistoryRepository history,
        IChannelDecision channel,
        string noritsuOutputRoot)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _noritsuOutputRoot = noritsuOutputRoot;
    }

    public SendResult SendToPrinter(int orderId)
    {
        var sent = new List<SentItem>();
        var skipped = new List<SkippedItem>();

        var order = _orders.GetOrder(orderId);
        if (order is null)
            throw new InvalidOperationException($"Order {orderId} not found.");

        var items = _orders.GetNoritsuItems(orderId);

        // Group by size/media
        var sizeGroups = items
            .GroupBy(i => new { i.SizeLabel, i.MediaType })
            .ToList();

        foreach (var group in sizeGroups)
        {
            var channelResult = _channel.Resolve(group.Key.SizeLabel, group.Key.MediaType);
            if (channelResult.ChannelNumber == 0)
            {
                skipped.Add(new SkippedItem(
                    $"{group.Key.SizeLabel} {group.Key.MediaType}",
                    "No channel assigned."));
                continue;
            }

            // TODO: call IPrinterWriter.WriteMrk() once interface is created
            var folderName = $"o{OrderHelpers.GetShortId(order.ExternalOrderId)}_{group.Key.SizeLabel}";

            foreach (var item in group)
            {
                sent.Add(new SentItem(item.Id, group.Key.SizeLabel,
                    channelResult.ChannelNumber, folderName));
            }
        }

        if (sent.Count > 0)
        {
            var sizes = string.Join(", ", sent.Select(s => s.SizeLabel).Distinct());
            _history.AddNote(orderId, $"Sent to printer: {sizes}");
        }

        return new SendResult(sent, skipped);
    }

    public void CheckPrintResults()
    {
        if (!Directory.Exists(_noritsuOutputRoot))
            return;

        foreach (var dir in Directory.GetDirectories(_noritsuOutputRoot, $"{SuccessPrefix}*"))
        {
            HandleCompletedFolder(dir, success: true);
        }

        foreach (var dir in Directory.GetDirectories(_noritsuOutputRoot, $"{ErrorPrefix}*"))
        {
            HandleCompletedFolder(dir, success: false);
        }
    }

    private void HandleCompletedFolder(string folderPath, bool success)
    {
        var folderName = Path.GetFileName(folderPath);

        if (success)
        {
            // TODO: match folder name to order+size, call _orders.SetItemsPrinted(), add history note
            AppLog.Info($"Noritsu completed: {folderName}");
        }
        else
        {
            AlertCollector.Error(AlertCategory.Printing,
                $"Noritsu rejected print job: {folderName}",
                detail: $"Folder: {folderPath}. Check Noritsu for error details.");
        }
    }
}
