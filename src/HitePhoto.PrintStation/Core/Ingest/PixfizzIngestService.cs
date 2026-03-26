using System.IO;
using HitePhoto.PrintStation.Core.Services;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Pixfizz ingest pipeline. Follows the flow:
/// 1. Poll OHD API for pending jobs (discovery only — order_number + job_id)
/// 2. Check disk for existing folder — if exists, verify; if not, download
/// 3. Download artwork + darkroom_ticket.txt from FTP
/// 4. Parse TXT from disk via PixfizzOrderParser (source of truth)
/// 5. Write to SQLite via unified IngestOrderWriter
/// 6. Background: mark /received after 24 hours verified
/// </summary>
public class PixfizzIngestService
{
    private readonly OhdApiSource _apiSource;
    private readonly PixfizzArtworkDownloader _downloader;
    private readonly PixfizzOrderParser _parser;
    private readonly OhdReceivedPusher _receivedPusher;
    private readonly IngestOrderWriter _writer;
    private readonly IOrderVerifier _verifier;
    private readonly IOrderRepository _orders;
    private readonly AppSettings _settings;

    public PixfizzIngestService(
        OhdApiSource apiSource,
        PixfizzArtworkDownloader downloader,
        PixfizzOrderParser parser,
        OhdReceivedPusher receivedPusher,
        IngestOrderWriter writer,
        IOrderVerifier verifier,
        IOrderRepository orders,
        AppSettings settings)
    {
        _apiSource = apiSource ?? throw new ArgumentNullException(nameof(apiSource));
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
        var jobId = raw.Metadata?.GetValueOrDefault("job_id") ?? "";
        var folderPath = IngestConstants.GetOrderFolderPath(_settings.OrderOutputPath, orderNumber);

        // Step 2: Check disk — if folder exists and downloaded, verify it
        if (Directory.Exists(folderPath) &&
            IngestConstants.MarkerExists(folderPath, IngestConstants.MarkerDownloadComplete))
        {
            VerifyAndRepair(orderNumber, folderPath);
            return;
        }

        // Step 3: Download (files only, no order building)
        var result = await _downloader.DownloadAsync(orderNumber, jobId, ct);

        if (!result.Success)
        {
            AppLog.Warn($"Pixfizz order {orderNumber}: {string.Join(", ", result.Errors)}");
            return; // Will retry next poll — no /received called
        }

        folderPath = result.FolderPath;

        // Step 4: Write download_complete marker
        IngestConstants.WriteMarker(folderPath, IngestConstants.MarkerDownloadComplete);

        // Step 5: Save raw API JSON for later /received marking
        SaveRawJson(folderPath, raw.RawData, orderNumber);

        // Step 6: Read TXT from disk, parse via PixfizzOrderParser
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
                ["job_id"] = jobId
            });

        var order = _parser.Parse(parseRaw);

        // Step 7: Write to SQLite
        _writer.WriteToSqlite(order, _settings.StoreId, "pixfizz", order.FolderPath ?? "");
    }

    private void VerifyAndRepair(string orderNumber, string folderPath)
    {
        var existingId = _orders.FindOrderId(orderNumber, _settings.StoreId);
        if (existingId == null) return;

        _verifier.VerifyOrder(orderNumber, folderPath, "pixfizz", existingId.Value);
    }

    private static void SaveRawJson(string folderPath, string rawData, string orderNumber)
    {
        try
        {
            var rawPath = Path.Combine(folderPath, "pixfizz_raw.json");
            if (!File.Exists(rawPath))
                File.WriteAllText(rawPath, rawData);
        }
        catch (Exception ex)
        {
            AlertCollector.Warn(AlertCategory.General,
                $"Could not write pixfizz_raw.json for {orderNumber}", ex: ex);
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
