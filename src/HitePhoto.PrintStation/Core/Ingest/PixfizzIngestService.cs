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
///
/// `/received` push: fires on every poll for any order with received_pushed=false.
/// Failures log-only, retry next cycle. The 24h cutoff that used to gate this
/// (ADR 0011 §9) was removed — see topics/printstation-pixfizz-discovery.md.
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

        // Push /received for any order with received_pushed=false. Runs every poll;
        // failed pushes log-only and retry next cycle until the flag flips.
        await PushReceivedAsync(ct);
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
            // No manifest. For a fresh order: write a stub. For an existing stub:
            // re-resolve download_status so the row tint reflects current OHD state
            // (e.g. unpaid → paid flips an unpaid film-dev order from yellow to blue).
            if (existingOrderId == null)
                IngestStubOrder(group, folderPath);
            else
                _orders.UpdateDownloadStatus(existingOrderId, ResolveStubStatusForPaid(head.Category));
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

    /// <summary>
    /// Status for a paid order that doesn't have a manifest. Film-dev terminal
    /// (no artwork ever); everything else is awaiting Pixfizz template emission.
    /// </summary>
    private static string ResolveStubStatusForPaid(string category) =>
        string.Equals(category, IngestConstants.CategoryFilmProcessing, StringComparison.OrdinalIgnoreCase)
            ? IngestConstants.StatusNoArtworkExpected
            : IngestConstants.StatusAwaitingFiles;

    private async Task PushReceivedAsync(CancellationToken ct)
    {
        if (_settings.DeveloperMode) return;

        var unreceived = _orders.GetUnreceivedPixfizzOrders(DateTime.Now);

        foreach (var (id, externalOrderId, jobId) in unreceived)
        {
            // Only acknowledge to Pixfizz if we actually have what we're supposed to
            // have. Stubs (no expected files) and orders missing files on disk both
            // skip — retry next cycle.
            if (!HasAllExpectedFiles(id))
            {
                AppLog.Info($"Skipping /received for {externalOrderId} — files not all on disk");
                continue;
            }

            try
            {
                await _receivedPusher.MarkReceivedAsync(externalOrderId, jobId, ct);
                _orders.MarkReceivedPushed(id);
                AppLog.Info($"Marked Pixfizz order {externalOrderId} (job {jobId}) as received");
            }
            catch (Exception ex)
            {
                AppLog.Info($"Failed to mark received for {externalOrderId} (will retry next cycle): {ex.Message}");
            }
        }
    }

    private bool HasAllExpectedFiles(string orderId)
    {
        var items = _orders.GetItems(orderId);
        var withFiles = items.Where(i => !string.IsNullOrEmpty(i.ImageFilepath)).ToList();
        if (withFiles.Count == 0) return false;  // pure stub — no artwork expected
        return withFiles.All(i => File.Exists(i.ImageFilepath));
    }
}
