using System.IO;
using HitePhoto.PrintStation.Core.Services;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Pixfizz ingest pipeline. Follows the flow:
/// 1. Poll OHD API for pending jobs
/// 2. Check disk for existing folder — if exists, verify; if not, download
/// 3. Download artwork + darkroom_ticket.txt from FTP
/// 4. Parse TXT (source of truth)
/// 5. Verify files (exists, >1KB, JPEG magic bytes)
/// 6. Write to SQLite — insert new or compare-and-repair existing
/// 7. Background: mark /received after 24 hours verified
/// </summary>
public class PixfizzIngestService
{
    private readonly OhdApiSource _apiSource;
    private readonly PixfizzArtworkDownloader _downloader;
    private readonly PixfizzOrderParser _parser;
    private readonly OhdReceivedPusher _receivedPusher;
    private readonly IOrderVerifier _verifier;
    private readonly IOrderRepository _orders;
    private readonly IHistoryRepository _history;
    private readonly AppSettings _settings;

    public PixfizzIngestService(
        OhdApiSource apiSource,
        PixfizzArtworkDownloader downloader,
        PixfizzOrderParser parser,
        OhdReceivedPusher receivedPusher,
        IOrderVerifier verifier,
        IOrderRepository orders,
        IHistoryRepository history,
        AppSettings settings)
    {
        _apiSource = apiSource ?? throw new ArgumentNullException(nameof(apiSource));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _receivedPusher = receivedPusher ?? throw new ArgumentNullException(nameof(receivedPusher));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Run one poll cycle. Called on a timer from the main app.
    /// </summary>
    public async Task PollAsync(CancellationToken ct)
    {
        if (!_settings.PixfizzEnabled) return;

        // Step 1: Poll API for pending jobs
        IReadOnlyList<RawOrder> pendingJobs;
        try
        {
            pendingJobs = await _apiSource.PollAsync(ct);
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Network,
                "Pixfizz API poll failed", ex: ex);
            return;
        }

        if (pendingJobs.Count == 0) return;

        foreach (var raw in pendingJobs)
        {
            try
            {
                await ProcessJobAsync(raw, ct);
            }
            catch (Exception ex)
            {
                AlertCollector.Error(AlertCategory.General,
                    $"Failed to process Pixfizz job {raw.ExternalOrderId}",
                    orderId: raw.ExternalOrderId, ex: ex);
            }
        }

        // Background: mark /received for orders verified > 24 hours
        await MarkOldOrdersReceivedAsync(ct);
    }

    private async Task ProcessJobAsync(RawOrder raw, CancellationToken ct)
    {
        var orderNumber = raw.ExternalOrderId;
        var folderPath = IngestConstants.GetOrderFolderPath(_settings.OrderOutputPath, orderNumber);

        // Step 2: Check disk — if folder exists, verify it
        if (Directory.Exists(folderPath) &&
            IngestConstants.MarkerExists(folderPath, IngestConstants.MarkerDownloadComplete))
        {
            // Already downloaded — verify and repair if needed
            await VerifyAndRepairAsync(orderNumber, folderPath, ct);
            return;
        }

        // Step 3-5: Parse API, download, verify
        var apiOrder = _parser.Parse(raw);
        var result = await _downloader.DownloadAsync(apiOrder, raw, ct);

        if (!result.Success)
        {
            AppLog.Warn($"Pixfizz order {orderNumber}: {string.Join(", ", result.Errors)}");
            return; // Will retry next poll — no /received called
        }

        // Write download_complete marker
        IngestConstants.WriteMarker(folderPath, IngestConstants.MarkerDownloadComplete);

        // Step 6: Write to SQLite
        WriteToSqlite(result.Order);
    }

    private Task VerifyAndRepairAsync(string orderNumber, string folderPath, CancellationToken ct)
    {
        var existingId = _orders.FindOrderId(orderNumber, _settings.StoreId);
        if (existingId == null) return Task.CompletedTask;

        return Task.Run(() => _verifier.VerifyOrder(orderNumber, folderPath, "pixfizz", existingId.Value), ct);
    }

    private void WriteToSqlite(UnifiedOrder order)
    {
        var existingId = _orders.FindOrderId(order.ExternalOrderId, _settings.StoreId);

        if (existingId == null)
        {
            var orderId = _orders.InsertOrder(order, _settings.StoreId);
            _history.AddNote(orderId, $"Order received at {DateTime.Now:g}");
            AppLog.Info($"Inserted Pixfizz order {order.ExternalOrderId} (id={orderId}, {order.Items.Count} items)");

            // Verify immediately after insert — same check every order gets
            var folderPath = order.FolderPath ?? IngestConstants.GetOrderFolderPath(_settings.OrderOutputPath, order.ExternalOrderId);
            _verifier.VerifyOrder(order.ExternalOrderId, folderPath, "pixfizz", orderId);
        }
        else
        {
            var folderPath = order.FolderPath ?? IngestConstants.GetOrderFolderPath(_settings.OrderOutputPath, order.ExternalOrderId);
            _verifier.VerifyOrder(order.ExternalOrderId, folderPath, "pixfizz", existingId.Value);
        }
    }

    /// <summary>
    /// Find orders that have been verified for > 24 hours and mark them as received on the OHD API.
    /// </summary>
    private async Task MarkOldOrdersReceivedAsync(CancellationToken ct)
    {
        if (_settings.DeveloperMode) return;

        var outputRoot = _settings.OrderOutputPath;
        if (string.IsNullOrEmpty(outputRoot) || !Directory.Exists(outputRoot)) return;

        var cutoff = DateTime.Now.AddHours(-24);

        foreach (var dir in Directory.GetDirectories(outputRoot))
        {
            // Must have download_complete but not received_pushed
            if (!IngestConstants.MarkerExists(dir, IngestConstants.MarkerDownloadComplete))
                continue;
            if (IngestConstants.MarkerExists(dir, IngestConstants.MarkerReceivedPushed))
                continue;

            // Check marker age
            var markerPath = Path.Combine(dir, "metadata", IngestConstants.MarkerDownloadComplete);
            var markerTime = File.GetCreationTime(markerPath);
            if (markerTime > cutoff)
                continue; // Not 24 hours old yet

            // Find the job ID from pixfizz_raw.json
            var rawPath = Path.Combine(dir, "pixfizz_raw.json");
            if (!File.Exists(rawPath)) continue;

            try
            {
                var rawJson = await File.ReadAllTextAsync(rawPath, ct);
                using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
                var jobId = JsonUtils.GetStr(doc.RootElement, "job_id");
                if (string.IsNullOrEmpty(jobId)) continue;

                await _receivedPusher.MarkReceivedAsync(jobId, ct);
                IngestConstants.WriteMarker(dir, IngestConstants.MarkerReceivedPushed);
            }
            catch (Exception ex)
            {
                AlertCollector.Warn(AlertCategory.Network,
                    $"Failed to mark received for {Path.GetFileName(dir)}",
                    ex: ex);
                // Will retry next cycle
            }
        }
    }
}
