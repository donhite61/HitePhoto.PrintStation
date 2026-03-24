using System.IO;
using HitePhoto.PrintStation.Data;
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
    private readonly IOrderRepository _orders;
    private readonly IHistoryRepository _history;
    private readonly OrderDb _db;
    private readonly AppSettings _settings;

    public PixfizzIngestService(
        OhdApiSource apiSource,
        PixfizzArtworkDownloader downloader,
        PixfizzOrderParser parser,
        OhdReceivedPusher receivedPusher,
        IOrderRepository orders,
        IHistoryRepository history,
        OrderDb db,
        AppSettings settings)
    {
        _apiSource = apiSource ?? throw new ArgumentNullException(nameof(apiSource));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _receivedPusher = receivedPusher ?? throw new ArgumentNullException(nameof(receivedPusher));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _db = db ?? throw new ArgumentNullException(nameof(db));
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

    /// <summary>
    /// Verify an existing order on disk. Reread TXT and compare to database.
    /// If they don't match, overwrite database (except history).
    /// </summary>
    private async Task VerifyAndRepairAsync(string orderNumber, string folderPath, CancellationToken ct)
    {
        var txtPath = Path.Combine(folderPath, "darkroom_ticket.txt");
        if (!File.Exists(txtPath)) return;

        // Reread TXT — the source of truth
        var txtContent = await File.ReadAllTextAsync(txtPath, ct);
        var txtResult = HitePhoto.Shared.Parsers.PixfizzTxtParser.ParseContent(txtContent, path => path);
        if (txtResult == null) return;

        var txtItems = TxtItemConverter.ToUnifiedItems(txtResult);

        // Verify all files on disk
        foreach (var item in txtItems)
        {
            if (string.IsNullOrEmpty(item.ImageFilepath)) continue;
            var error = OrderHelpers.VerifyFile(item.ImageFilepath);
            if (error != null)
            {
                AlertCollector.Warn(AlertCategory.DataQuality,
                    $"File verification failed during repair check: {item.ImageFilename}",
                    orderId: orderNumber,
                    detail: error);
            }
        }

        // TODO: compare TXT data against SQLite, overwrite if mismatch
        // Don't overwrite history. Add "Repaired at" note if changes made.
    }

    /// <summary>
    /// Write a verified order to SQLite. Insert new or compare-and-repair existing.
    /// Never overwrites history.
    /// </summary>
    private void WriteToSqlite(UnifiedOrder order)
    {
        using var conn = _db.OpenConnection();

        // Check if order already exists
        int? existingId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM orders WHERE external_order_id = @eid AND pickup_store_id = @store";
            cmd.Parameters.AddWithValue("@eid", order.ExternalOrderId);
            cmd.Parameters.AddWithValue("@store", _settings.StoreId);
            var result = cmd.ExecuteScalar();
            existingId = result != null ? Convert.ToInt32(result) : null;
        }

        if (existingId == null)
        {
            InsertOrder(conn, order);
        }
        else
        {
            CompareAndRepair(conn, existingId.Value, order);
        }
    }

    private void InsertOrder(Microsoft.Data.Sqlite.SqliteConnection conn, UnifiedOrder order)
    {
        using var transaction = conn.BeginTransaction();

        int orderId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO orders (
                    external_order_id, order_source_id, source_code,
                    customer_first_name, customer_last_name, customer_email, customer_phone,
                    order_status_id, status_code, pickup_store_id,
                    total_amount, payment_status, special_instructions,
                    order_type, is_rush, ordered_at, folder_path, download_status
                ) VALUES (
                    @eid, 1, 'pixfizz',
                    @fname, @lname, @email, @phone,
                    1, 'new', @store,
                    @total, 'paid', @notes,
                    @type, @rush, @ordered, @folder, @status
                );
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@eid", order.ExternalOrderId);
            cmd.Parameters.AddWithValue("@fname", order.CustomerFirstName ?? "");
            cmd.Parameters.AddWithValue("@lname", order.CustomerLastName ?? "");
            cmd.Parameters.AddWithValue("@email", order.CustomerEmail ?? "");
            cmd.Parameters.AddWithValue("@phone", order.CustomerPhone ?? "");
            cmd.Parameters.AddWithValue("@store", _settings.StoreId);
            cmd.Parameters.AddWithValue("@total", order.OrderTotal ?? 0m);
            cmd.Parameters.AddWithValue("@notes", order.Notes ?? "");
            cmd.Parameters.AddWithValue("@type", order.OrderType ?? "");
            cmd.Parameters.AddWithValue("@rush", order.IsRush ? 1 : 0);
            cmd.Parameters.AddWithValue("@ordered", order.OrderedAt?.ToString("O") ?? DateTime.Now.ToString("O"));
            cmd.Parameters.AddWithValue("@folder", order.FolderPath ?? "");
            cmd.Parameters.AddWithValue("@status", order.DownloadStatus);
            orderId = Convert.ToInt32(cmd.ExecuteScalar()!);
        }

        // Insert items
        foreach (var item in order.Items)
        {
            InsertItem(conn, orderId, item);
        }

        _history.AddNote(orderId, $"Order received at {DateTime.Now:g}");
        transaction.Commit();

        AppLog.Info($"Inserted Pixfizz order {order.ExternalOrderId} (id={orderId}, {order.Items.Count} items)");
    }

    private static void InsertItem(Microsoft.Data.Sqlite.SqliteConnection conn, int orderId, UnifiedOrderItem item)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO order_items (
                order_id, size_label, media_type, quantity,
                image_filename, image_filepath, original_image_filepath,
                is_noritsu, options_json
            ) VALUES (
                @oid, @size, @media, @qty,
                @fname, @fpath, @orig,
                @noritsu, @options
            )
            """;
        cmd.Parameters.AddWithValue("@oid", orderId);
        cmd.Parameters.AddWithValue("@size", item.SizeLabel ?? "");
        cmd.Parameters.AddWithValue("@media", item.MediaType ?? "");
        cmd.Parameters.AddWithValue("@qty", item.Quantity);
        cmd.Parameters.AddWithValue("@fname", item.ImageFilename ?? "");
        cmd.Parameters.AddWithValue("@fpath", item.ImageFilepath ?? "");
        cmd.Parameters.AddWithValue("@orig", item.OriginalImageFilepath ?? item.ImageFilepath ?? "");
        cmd.Parameters.AddWithValue("@noritsu", item.IsNoritsu ? 1 : 0);
        cmd.Parameters.AddWithValue("@options", item.Options.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(item.Options)
            : "[]");
        cmd.ExecuteNonQuery();
    }

    private void CompareAndRepair(Microsoft.Data.Sqlite.SqliteConnection conn, int existingId, UnifiedOrder order)
    {
        // TODO: compare TXT data against existing DB record
        // Overwrite source fields (customer, items, total) if they don't match TXT
        // Never overwrite: history, hold state, printed state, notes added by operator
        // Add "Repaired at {timestamp}" note if changes were made
        AppLog.Info($"Order {order.ExternalOrderId} already exists (id={existingId}) — verify/repair not yet implemented");
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
