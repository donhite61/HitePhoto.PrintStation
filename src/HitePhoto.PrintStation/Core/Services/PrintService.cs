using System.IO;
using HitePhoto.PrintStation.Core.Decisions;
using HitePhoto.PrintStation.Core.Models;
using HitePhoto.PrintStation.Core.Processing;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core.Services;

public class PrintService : IPrintService
{
    private readonly IOrderRepository _orders;
    private readonly IHistoryRepository _history;
    private readonly IChannelDecision _channel;
    private readonly IPrinterWriter _writer;
    private readonly AppSettings _settings;
    private readonly string _noritsuOutputRoot;

    private const char SuccessPrefix = 'e';
    private const char ErrorPrefix = 'q';

    public PrintService(
        IOrderRepository orders,
        IHistoryRepository history,
        IChannelDecision channel,
        IPrinterWriter writer,
        AppSettings settings,
        string noritsuOutputRoot)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _noritsuOutputRoot = noritsuOutputRoot;
    }

    public SendResult SendToPrinter(int orderId, HashSet<string>? sizeFilter = null)
    {
        var sent = new List<SentItem>();
        var skipped = new List<SkippedItem>();

        var order = _orders.GetOrder(orderId);
        if (order is null)
            throw new InvalidOperationException($"Order {orderId} not found.");

        var items = _orders.GetNoritsuItems(orderId);
        if (items.Count == 0)
        {
            skipped.Add(new SkippedItem("(all)", "No printable items found."));
            return new SendResult(sent, skipped);
        }

        // Group by size + options (border type etc. affects channel routing)
        var sizeGroups = items
            .Where(i => sizeFilter == null || sizeFilter.Contains($"{i.SizeLabel}|{OrderHelpers.BuildOptionsKey(i.OptionsJson)}"))
            .GroupBy(i => new { i.SizeLabel, OptionsKey = OrderHelpers.BuildOptionsKey(i.OptionsJson) })
            .ToList();

        foreach (var group in sizeGroups)
        {
            var routingKey = OrderHelpers.BuildRoutingKey(group.Key.SizeLabel, group.Key.OptionsKey);
            var channelResult = _channel.ResolveByKey(routingKey);
            if (channelResult.ChannelNumber == 0)
            {
                skipped.Add(new SkippedItem(
                    $"{group.Key.SizeLabel} {group.Key.OptionsKey}".Trim(),
                    "No channel assigned."));
                continue;
            }

            try
            {
                var groupItems = group.ToList();
                string folderName;
                string sizeLabel;
                int channelNumber;

                // Layout branch: apply layout processing before writing MRK
                if (channelResult.LayoutName is not null)
                {
                    var layout = _settings.Layouts.FirstOrDefault(l =>
                        l.Name.Equals(channelResult.LayoutName, StringComparison.OrdinalIgnoreCase));

                    if (layout is null)
                    {
                        AlertCollector.Error(AlertCategory.Printing,
                            $"Layout '{channelResult.LayoutName}' not found in settings",
                            orderId: order.ExternalOrderId,
                            detail: $"Attempted: print {group.Key.SizeLabel} using layout '{channelResult.LayoutName}'. " +
                                    $"Expected: layout defined in Settings → Layouts. " +
                                    $"Found: no matching layout. " +
                                    $"Context: channel_mappings routing_key='{channelResult.RoutingKey}'. " +
                                    $"State: order {order.ExternalOrderId}, {groupItems.Count} items.");
                        skipped.Add(new SkippedItem(
                            $"{group.Key.SizeLabel} {group.Key.OptionsKey}".Trim(),
                            $"Layout '{channelResult.LayoutName}' not found."));
                        continue;
                    }

                    var layoutItems = new List<OrderItemRecord>();
                    foreach (var item in groupItems)
                    {
                        if (string.IsNullOrEmpty(item.ImageFilepath))
                        {
                            AlertCollector.Error(AlertCategory.Printing,
                                "Image path missing for layout processing",
                                orderId: order.ExternalOrderId,
                                detail: $"Attempted: layout '{layout.Name}' on item {item.Id}. " +
                                        $"Expected: non-empty ImageFilepath. Found: null/empty. " +
                                        $"Context: size {group.Key.SizeLabel}. " +
                                        $"State: order {order.ExternalOrderId}.");
                            continue;
                        }

                        string sourcePath = LayoutProcessor.ResolveSourcePath(item.ImageFilepath, order.FolderPath);
                        string outputPath = LayoutProcessor.BuildLayoutPath(sourcePath, order.FolderPath, layout.Name);
                        LayoutProcessor.ApplyLayout(sourcePath, outputPath, layout);

                        layoutItems.Add(item with { ImageFilepath = outputPath });
                    }

                    if (layoutItems.Count == 0)
                    {
                        skipped.Add(new SkippedItem(
                            $"{group.Key.SizeLabel} {group.Key.OptionsKey}".Trim(),
                            "No valid images after layout processing."));
                        continue;
                    }

                    sizeLabel = layout.TargetSizeLabel;
                    channelNumber = layout.TargetChannelNumber;
                    folderName = _writer.WritePrintJob(
                        order.ExternalOrderId,
                        sizeLabel,
                        channelNumber,
                        layoutItems);
                }
                else
                {
                    // Standard path — no layout, direct to printer
                    sizeLabel = group.Key.SizeLabel;
                    channelNumber = channelResult.ChannelNumber;
                    folderName = _writer.WritePrintJob(
                        order.ExternalOrderId,
                        sizeLabel,
                        channelNumber,
                        groupItems);
                }

                // Mark items as printed
                var printedIds = groupItems.Select(i => i.Id).ToList();
                _orders.SetItemsPrinted(printedIds);

                foreach (var item in groupItems)
                {
                    sent.Add(new SentItem(item.Id, sizeLabel,
                        channelNumber, folderName));
                }
            }
            catch (Exception ex)
            {
                AlertCollector.Error(AlertCategory.Printing,
                    $"Print failed for {group.Key.SizeLabel} in order {order.ExternalOrderId}",
                    orderId: order.ExternalOrderId, ex: ex);
                skipped.Add(new SkippedItem(
                    $"{group.Key.SizeLabel} {group.Key.OptionsKey}".Trim(),
                    ex.Message));
            }
        }

        if (sent.Count > 0)
        {
            var sizes = string.Join(", ", sent.Select(s => s.SizeLabel).Distinct());
            _history.AddNote(orderId, $"Sent to printer: {sizes}");

            // Auto-set order.is_printed if all items are now printed
            if (_orders.AreAllItemsPrinted(orderId))
                _orders.SetOrderPrinted(orderId, true);
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
            // TODO: match folder name to order+size, confirm printed status, add history note
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
