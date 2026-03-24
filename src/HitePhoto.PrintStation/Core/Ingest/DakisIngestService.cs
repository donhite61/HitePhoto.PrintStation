using System.IO;
using HitePhoto.PrintStation.Data;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Dakis ingest pipeline. Follows the flow:
/// 1. Watch incoming folder for new order folders
/// 2. Check for metadata/download_complete marker (Dakis still writing if absent)
/// 3. Parse order.yml
/// 4. Verify files (exists, >1KB, JPEG magic bytes)
/// 5. Write to SQLite — insert new or compare-and-repair existing
/// </summary>
public class DakisIngestService
{
    private readonly DakisOrderParser _parser;
    private readonly IOrderRepository _orders;
    private readonly IHistoryRepository _history;
    private readonly OrderDb _db;
    private readonly AppSettings _settings;

    public DakisIngestService(
        DakisOrderParser parser,
        IOrderRepository orders,
        IHistoryRepository history,
        OrderDb db,
        AppSettings settings)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Scan the Dakis watch folder for new/updated orders.
    /// Called on a timer or triggered by FileSystemWatcher.
    /// </summary>
    public void ScanFolder()
    {
        if (!_settings.DakisEnabled) return;

        var watchFolder = _settings.DakisWatchFolder;
        if (string.IsNullOrEmpty(watchFolder) || !Directory.Exists(watchFolder))
            return;

        foreach (var orderDir in Directory.GetDirectories(watchFolder))
        {
            try
            {
                ProcessOrderFolder(orderDir);
            }
            catch (Exception ex)
            {
                AlertCollector.Error(AlertCategory.Parsing,
                    $"Failed to process Dakis folder: {Path.GetFileName(orderDir)}",
                    orderId: Path.GetFileName(orderDir), ex: ex);
            }
        }
    }

    private void ProcessOrderFolder(string folderPath)
    {
        // Step 2: Check for download_complete marker
        if (!IngestConstants.MarkerExists(folderPath, IngestConstants.MarkerDownloadComplete))
            return; // Dakis is still writing

        var orderId = Path.GetFileName(folderPath);

        // Read order.yml
        var ymlPath = Path.Combine(folderPath, "order.yml");
        if (!File.Exists(ymlPath))
        {
            AlertCollector.Warn(AlertCategory.Parsing,
                $"order.yml not found in {orderId}",
                orderId: orderId);
            return;
        }

        var ymlContent = File.ReadAllText(ymlPath);

        var raw = new RawOrder(
            ExternalOrderId: orderId,
            SourceName: "dakis",
            RawData: ymlContent,
            Metadata: new Dictionary<string, string> { ["folder_path"] = folderPath });

        // Step 3: Parse
        var order = _parser.Parse(raw);

        // Step 4: Verify files
        var errors = new List<string>();
        foreach (var item in order.Items)
        {
            if (string.IsNullOrEmpty(item.ImageFilepath)) continue;
            if (!item.IsNoritsu) continue; // Non-Noritsu items don't need files checked here

            var error = OrderHelpers.VerifyFile(item.ImageFilepath);
            if (error != null)
                errors.Add($"{item.ImageFilename}: {error}");
        }

        if (errors.Count > 0)
        {
            order = order with
            {
                DownloadStatus = IngestConstants.StatusDownloadError,
                DownloadErrors = errors
            };
            AlertCollector.Warn(AlertCategory.DataQuality,
                $"Dakis order {orderId}: {errors.Count} file(s) failed verification",
                orderId: orderId);
        }
        else
        {
            order = order with { DownloadStatus = IngestConstants.StatusReady };
        }

        // Step 5: Write to SQLite
        WriteToSqlite(order);
    }

    private void WriteToSqlite(UnifiedOrder order)
    {
        using var conn = _db.OpenConnection();

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
            // TODO: compare-and-repair (same as Pixfizz)
            AppLog.Info($"Dakis order {order.ExternalOrderId} already exists (id={existingId})");
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
                    @eid, 2, 'dakis',
                    @fname, @lname, @email, @phone,
                    1, 'new', @store,
                    @total, @paid, @notes,
                    @type, 0, @ordered, @folder, @status
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
            cmd.Parameters.AddWithValue("@paid", order.Paid ? "paid" : "unpaid");
            cmd.Parameters.AddWithValue("@notes", order.Notes ?? "");
            cmd.Parameters.AddWithValue("@type", order.OrderType ?? "");
            cmd.Parameters.AddWithValue("@ordered", order.OrderedAt?.ToString("O") ?? DateTime.Now.ToString("O"));
            cmd.Parameters.AddWithValue("@folder", order.FolderPath ?? "");
            cmd.Parameters.AddWithValue("@status", order.DownloadStatus);
            orderId = Convert.ToInt32(cmd.ExecuteScalar()!);
        }

        foreach (var item in order.Items)
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

        _history.AddNote(orderId, $"Order received at {DateTime.Now:g}");
        transaction.Commit();

        AppLog.Info($"Inserted Dakis order {order.ExternalOrderId} (id={orderId}, {order.Items.Count} items)");
    }
}
