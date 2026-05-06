using System.IO;
using HitePhoto.PrintStation.Core.Services;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Pixfizz ingest pipeline (API + JSON manifest).
///
/// Pre-rebuild this service used the legacy /Darkroom TXT path. After 2026-05-05
/// it sources everything from OHD API + the order-folder JSON manifest:
///
///   1. OhdJobsPoller.PollAsync()         → groups of jobs by order_number
///   2. PixfizzManifestReader.FetchAsync() → per-image filename / size / qty
///   3. PixfizzFtpDownloader.DownloadByManifestAsync() → files into prints/format/qty
///   4. PixfizzApiJsonParser.Parse()       → UnifiedOrder
///   5. IngestOrderWriter.WriteToSqlite()  → DB
///
/// Stub orders (unpaid OR paid-but-Pixfizz-hasn't-emitted-JSON-yet) skip steps
/// 2–3 and produce a stub UnifiedOrder with IsNoritsu=false items (bypasses the
/// writer's file-existence validation). They show in the order tree so the
/// operator knows about them.
///
/// Stub upgrade: when a paid stub already in the DB has its manifest appear on
/// FTP on a later poll, this service swaps the stub items for full items via
/// IOrderRepository.ReplaceItems. The marker file 'download_complete' is the
/// stub-vs-full discriminator.
/// </summary>
public class PixfizzIngestService
{
    private readonly OhdJobsPoller _poller;
    private readonly PixfizzManifestReader _manifestReader;
    private readonly PixfizzFtpDownloader _ftpDownloader;
    private readonly PixfizzApiJsonParser _parser;
    private readonly OhdReceivedPusher _receivedPusher;
    private readonly IngestOrderWriter _writer;
    private readonly IOrderRepository _orders;
    private readonly IHistoryRepository _history;
    private readonly AppSettings _settings;

    public PixfizzIngestService(
        OhdJobsPoller poller,
        PixfizzManifestReader manifestReader,
        PixfizzFtpDownloader ftpDownloader,
        PixfizzApiJsonParser parser,
        OhdReceivedPusher receivedPusher,
        IngestOrderWriter writer,
        IOrderRepository orders,
        IHistoryRepository history,
        AppSettings settings)
    {
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _manifestReader = manifestReader ?? throw new ArgumentNullException(nameof(manifestReader));
        _ftpDownloader = ftpDownloader ?? throw new ArgumentNullException(nameof(ftpDownloader));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _receivedPusher = receivedPusher ?? throw new ArgumentNullException(nameof(receivedPusher));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task PollAsync(CancellationToken ct)
    {
        if (!_settings.PixfizzEnabled) return;

        IReadOnlyList<OhdOrderGroup> groups;
        try
        {
            groups = await _poller.PollAsync(ct);
        }
        catch (Exception ex)
        {
            // OHD API can return transient 401s / 5xxs that recover on the next 30s tick.
            // Log-only here; dedup/backoff layer (topics/alert-system.md) will alert when
            // the failure persists across multiple cycles.
            AppLog.Info($"OHD poll failed (will retry next cycle): {ex.Message}");
            return;
        }

        foreach (var group in groups)
        {
            try
            {
                await ProcessGroupAsync(group, ct);
            }
            catch (Exception ex)
            {
                AlertCollector.Error(AlertCategory.General,
                    $"Failed to process Pixfizz order {group.OrderNumber}",
                    orderId: group.OrderNumber, ex: ex);
            }
        }

        // Auto-/received push for orders verified > 24h (kept from pre-rebuild;
        // Phase C will replace this with operator-driven push at Mark Printed time).
        await MarkOldOrdersReceivedAsync(ct);
    }

    private async Task ProcessGroupAsync(OhdOrderGroup group, CancellationToken ct)
    {
        var orderNumber = group.OrderNumber;
        var head = group.Jobs[0];
        bool isPaid = head.OrderStatus.Equals(IngestConstants.OhdOrderStatusConfirmed, StringComparison.OrdinalIgnoreCase);
        var folderPath = IngestConstants.GetOrderFolderPath(_settings.OrderOutputPath, orderNumber);

        var existingId = _orders.FindOrderId(orderNumber, _settings.StoreId);

        if (existingId != null)
        {
            // Stub upgrade path: paid order in DB without download_complete marker.
            bool downloaded = IngestConstants.MarkerExists(folderPath, IngestConstants.MarkerDownloadComplete);
            if (downloaded || !isPaid) return;

            await IngestPaidAsync(group, folderPath, existingId, ct);
            return;
        }

        if (isPaid)
            await IngestPaidAsync(group, folderPath, existingOrderId: null, ct);
        else
            IngestStubOrder(group, folderPath);
    }

    private async Task IngestPaidAsync(OhdOrderGroup group, string folderPath, string? existingOrderId, CancellationToken ct)
    {
        var head = group.Jobs[0];

        var manifest = await _manifestReader.FetchAsync(head.OrderNumber, head.OrderIdHash, ct);
        if (manifest == null)
        {
            // No manifest yet (e.g. Pixfizz hasn't attached the JSON Fulfillment
            // Template to this product category). Stub the new order so the
            // operator sees it; existing stubs are left alone for the next poll.
            if (existingOrderId == null)
                IngestStubOrder(group, folderPath);
            return;
        }

        Directory.CreateDirectory(folderPath);
        var apiJobsByJobId = group.Jobs.ToDictionary(j => j.JobId);
        await _ftpDownloader.DownloadByManifestAsync(
            head.OrderNumber, head.OrderIdHash, manifest, apiJobsByJobId, folderPath, ct);

        IngestConstants.WriteMarker(folderPath, IngestConstants.MarkerDownloadComplete);

        var unified = _parser.Parse(group.Jobs, manifest, folderPath);

        if (existingOrderId == null)
        {
            _writer.WriteToSqlite(unified, _settings.StoreId, "pixfizz", folderPath);
            AppLog.Info($"Pixfizz ingested order {head.OrderNumber} ({unified.Items.Count} items)");
        }
        else
        {
            _orders.ReplaceItems(existingOrderId, unified.Items);
            _history.AddNoteIfNew(existingOrderId, "Files received from Pixfizz — stub replaced with full items", "ingest");
            AppLog.Info($"Pixfizz upgraded stub {head.OrderNumber} → full order ({unified.Items.Count} items)");
        }
    }

    private void IngestStubOrder(OhdOrderGroup group, string folderPath)
    {
        var unified = _parser.Parse(group.Jobs, manifest: null, folderPath);
        _writer.WriteToSqlite(unified, _settings.StoreId, "pixfizz", folderPath);

        // The row tint shows there's a reason; this records what the reason is.
        // AddNoteIfNew dedupes — won't spam on repeat polls.
        var orderId = _orders.FindOrderId(unified.ExternalOrderId, _settings.StoreId);
        var note = ExplainStubStatus(unified.DownloadStatus);
        if (orderId != null && !string.IsNullOrEmpty(note))
            _history.AddNoteIfNew(orderId, note, "ingest");

        AppLog.Info($"Pixfizz ingested stub for {group.OrderNumber} ({unified.Items.Count} jobs, status={unified.DownloadStatus})");
    }

    private static string ExplainStubStatus(string downloadStatus) => downloadStatus switch
    {
        IngestConstants.StatusUnpaid             => "Unpaid — Pixfizz withholds artwork until paid",
        IngestConstants.StatusAwaitingFiles      => "Paid, but Pixfizz hasn't emitted the JSON manifest for this category yet",
        IngestConstants.StatusNoArtworkExpected  => "No artwork expected — Film Processing (lab will produce)",
        _                                        => "",
    };

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
            }
        }
    }
}
