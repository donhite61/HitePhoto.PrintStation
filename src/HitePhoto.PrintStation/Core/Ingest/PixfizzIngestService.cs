using System.IO;
using HitePhoto.PrintStation.Core.Services;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Pixfizz ingest pipeline. Follows the flow:
/// 1. Scan FTP /Artwork for order folders with JSON manifests
/// 2. Check disk for existing folder — if exists, verify; if not, download
/// 3. Download artwork + darkroom TXT from FTP using orderId from JSON
/// 4. Parse TXT from disk via PixfizzOrderParser (source of truth)
/// 5. Write to SQLite via unified IngestOrderWriter
/// 6. Background: mark /received after 24 hours verified
/// </summary>
public class PixfizzIngestService
{
    private readonly PixfizzFtpScanner _scanner;
    private readonly PixfizzArtworkDownloader _downloader;
    private readonly PixfizzOrderParser _parser;
    private readonly OhdReceivedPusher _receivedPusher;
    private readonly IngestOrderWriter _writer;
    private readonly IOrderVerifier _verifier;
    private readonly IOrderRepository _orders;
    private readonly AppSettings _settings;

    public PixfizzIngestService(
        PixfizzFtpScanner scanner,
        PixfizzArtworkDownloader downloader,
        PixfizzOrderParser parser,
        OhdReceivedPusher receivedPusher,
        IngestOrderWriter writer,
        IOrderVerifier verifier,
        IOrderRepository orders,
        AppSettings settings)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _receivedPusher = receivedPusher ?? throw new ArgumentNullException(nameof(receivedPusher));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Run one poll cycle. Called on a timer from the main app.
    /// </summary>
    public async Task PollAsync(CancellationToken ct)
    {
        if (!_settings.PixfizzEnabled) return;

        // Step 1: Scan FTP for orders
        List<PixfizzFtpOrder> ftpOrders;
        try
        {
            ftpOrders = await _scanner.ScanAsync(ct);
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Network,
                "Pixfizz FTP scan failed", ex: ex);
            return;
        }

        if (ftpOrders.Count == 0) return;

        foreach (var ftpOrder in ftpOrders)
        {
            try
            {
                await ProcessFtpOrderAsync(ftpOrder, ct);
            }
            catch (Exception ex)
            {
                AlertCollector.Error(AlertCategory.General,
                    $"Failed to process Pixfizz order {ftpOrder.OrderNumber}",
                    orderId: ftpOrder.OrderNumber, ex: ex);
            }
        }

        // Background: mark /received for orders verified > 24 hours
        await MarkOldOrdersReceivedAsync(ct);
    }

    private async Task ProcessFtpOrderAsync(PixfizzFtpOrder ftpOrder, CancellationToken ct)
    {
        var orderNumber = ftpOrder.OrderNumber;
        var orderId = ftpOrder.OrderId;
        var folderPath = IngestConstants.GetOrderFolderPath(_settings.OrderOutputPath, orderNumber);

        // Already downloaded and ingested? Just verify.
        if (Directory.Exists(folderPath) &&
            IngestConstants.MarkerExists(folderPath, IngestConstants.MarkerDownloadComplete))
        {
            VerifyAndRepair(orderNumber, folderPath);
            return;
        }

        // Download artwork + TXT using the orderId (matches FTP filenames)
        var result = await _downloader.DownloadAsync(orderNumber, orderId, ct);

        if (!result.Success)
        {
            AlertCollector.Error(AlertCategory.Network,
                $"Pixfizz download failed for {orderNumber}",
                orderId: orderNumber,
                detail: $"Attempted: download order {orderNumber} (orderId={orderId}). " +
                        $"Expected: successful download. " +
                        $"Found: {string.Join(", ", result.Errors)}. " +
                        $"Context: FTP scan. State: order will retry next poll.");
            return;
        }

        folderPath = result.FolderPath;

        // Write download_complete marker
        IngestConstants.WriteMarker(folderPath, IngestConstants.MarkerDownloadComplete);

        // Read TXT from disk, parse via PixfizzOrderParser
        var txtPath = Path.Combine(folderPath, "darkroom_ticket.txt");
        if (!File.Exists(txtPath))
        {
            AlertCollector.Error(AlertCategory.DataQuality,
                $"Pixfizz darkroom_ticket.txt missing after download",
                orderId: orderNumber,
                detail: $"Attempted: read TXT from '{txtPath}'. Expected: file exists after download. " +
                        $"Found: file missing. Context: order {orderNumber}, folder '{folderPath}'. " +
                        $"State: cannot parse order without TXT.");
            return;
        }

        var txtContent = await File.ReadAllTextAsync(txtPath, ct);

        var parseRaw = new RawOrder(
            ExternalOrderId: orderNumber,
            SourceName: "pixfizz",
            RawData: txtContent,
            Metadata: new Dictionary<string, string>
            {
                ["folder_path"] = folderPath,
                ["order_id"] = orderId
            });

        var order = _parser.Parse(parseRaw);

        // Write to SQLite
        _writer.WriteToSqlite(order, _settings.StoreId, "pixfizz", order.FolderPath ?? "");

        AppLog.Info($"Pixfizz ingested order {orderNumber} ({order.Items.Count} items)");
    }

    private void VerifyAndRepair(string orderNumber, string folderPath)
    {
        var existingId = _orders.FindOrderId(orderNumber, _settings.StoreId);
        if (existingId == null) return;

        _verifier.VerifyOrder(orderNumber, folderPath, "pixfizz", existingId);
    }

    /// <summary>
    /// Find Pixfizz orders older than 24 hours that haven't been marked received on the OHD API.
    /// </summary>
    private async Task MarkOldOrdersReceivedAsync(CancellationToken ct)
    {
        if (_settings.DeveloperMode) return;

        var cutoff = DateTime.Now.AddHours(-24);
        var unreceived = _orders.GetUnreceivedPixfizzOrders(cutoff);

        foreach (var (id, externalOrderId, jobId) in unreceived)
        {
            try
            {
                await _receivedPusher.MarkReceivedAsync(jobId, ct);
                _orders.MarkReceivedPushed(id);
                AppLog.Info($"Marked Pixfizz order {externalOrderId} (job {jobId}) as received");
            }
            catch (Exception ex)
            {
                AlertCollector.Error(AlertCategory.Network,
                    $"Failed to mark received for {externalOrderId}",
                    orderId: externalOrderId, ex: ex);
                // Will retry next cycle
            }
        }
    }
}
