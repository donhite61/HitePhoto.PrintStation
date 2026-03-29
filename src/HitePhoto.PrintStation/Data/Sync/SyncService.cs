using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Data.Sync;

public class SyncService : ISyncService
{
    private readonly OrderDb _localDb;
    private readonly PrintStationDb _remoteDb;
    private readonly OutboxRepository _outbox;
    private readonly AppSettings _settings;

    public SyncService(OrderDb localDb, PrintStationDb remoteDb, OutboxRepository outbox, AppSettings settings)
    {
        _localDb = localDb;
        _remoteDb = remoteDb;
        _outbox = outbox;
        _settings = settings;
    }

    // ── Push ─────────────────────────────────────────────────────────────

    public async Task<bool> PushAsync(string tableName, int recordId, string operation, string payloadJson)
    {
        try
        {
            // Only the pickup store pushes new order inserts (prevents duplicates).
            // All other operations (hold, status, notes, printed) push from any store.
            if (operation == "insert_order")
            {
                var orderId = GetOrderIdFromPayload(payloadJson);
                if (orderId > 0 && !IsOurOrder(orderId))
                    return true; // not our order to insert — treat as success
            }

            var success = await ExecutePushAsync(operation, payloadJson);
            if (success)
                return true;

            // Push failed — queue in outbox
            _outbox.Enqueue(tableName, recordId, operation, payloadJson);
            return false;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                $"MariaDB push failed: {operation}",
                detail: $"Attempted: push {operation} for {tableName} record {recordId}. " +
                        $"Expected: MariaDB updated. " +
                        $"Found: {ex.GetType().Name}: {ex.Message}. " +
                        $"Context: immediate sync push. " +
                        $"State: queued in sync_outbox for retry.",
                ex: ex);
            _outbox.Enqueue(tableName, recordId, operation, payloadJson);
            return false;
        }
    }

    private async Task<bool> ExecutePushAsync(string operation, string payloadJson)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson)
            ?? throw new InvalidOperationException("Empty push payload");

        switch (operation)
        {
            case "insert_order":
                return await PushInsertOrderAsync(payload);

            case "set_hold":
            {
                var orderId = payload["orderId"].GetInt32();
                var isHeld = payload["isHeld"].GetBoolean();
                var remoteId = await ResolveRemoteOrderIdAsync(orderId);
                if (remoteId == null) return false;
                return await _remoteDb.ToggleHoldAsync(remoteId.Value, isHeld);
            }

            case "update_status":
            {
                var orderId = payload["orderId"].GetInt32();
                var statusCode = payload["statusCode"].GetString()!;
                var remoteId = await ResolveRemoteOrderIdAsync(orderId);
                if (remoteId == null) return false;
                var statusId = SyncMapper.StatusCodeToStatusId(statusCode);
                return await _remoteDb.UpdateOrderStatusAsync(remoteId.Value, statusId);
            }

            case "set_notified":
            {
                var orderId = payload["orderId"].GetInt32();
                var remoteId = await ResolveRemoteOrderIdAsync(orderId);
                if (remoteId == null) return false;
                return await _remoteDb.UpdateOrderStatusAsync(remoteId.Value, 5); // notified
            }

            case "set_items_printed":
            {
                var orderId = payload["orderId"].GetInt32();
                var itemIds = payload["itemIds"].Deserialize<List<int>>()!;
                var remoteId = await ResolveRemoteOrderIdAsync(orderId);
                if (remoteId == null) return false;
                // Mark each item as printed — need to push full item state
                foreach (var localItemId in itemIds)
                    await _remoteDb.UpdateItemPrintedAsync(localItemId, DateTime.Now);
                return true;
            }

            case "set_current_location":
            {
                // No direct MariaDB method for this — covered by order upsert
                return true;
            }

            case "add_note":
            {
                var orderId = payload["orderId"].GetInt32();
                var note = payload["note"].GetString()!;
                var createdBy = payload.TryGetValue("createdBy", out var cb) ? cb.GetString() : "";
                var remoteId = await ResolveRemoteOrderIdAsync(orderId);
                if (remoteId == null) return false;
                return await _remoteDb.AddNoteAsync(remoteId.Value, null, note);
            }

            default:
                AppLog.Warn($"SyncService: unknown push operation '{operation}'");
                return true; // don't re-queue unknown ops
        }
    }

    private async Task<bool> PushInsertOrderAsync(Dictionary<string, JsonElement> payload)
    {
        var localOrderId = payload["localOrderId"].GetInt32();

        // Read the full order from SQLite to push
        using var conn = _localDb.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT external_order_id, pickup_store_id, order_source_id, order_status_id,
                   customer_first_name, customer_last_name, customer_email, customer_phone,
                   total_amount, is_held, is_transfer, transfer_store_id,
                   special_instructions, folder_path, delivery_method_id,
                   ordered_at, pixfizz_job_id, download_status, source_code
            FROM orders WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", localOrderId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return false;

        var externalOrderId = reader.GetString(0);
        var pickupStoreId = reader.GetInt32(1);
        var sourceCode = reader.IsDBNull(18) ? "pixfizz" : reader.GetString(18);
        var orderSourceId = SyncMapper.SourceCodeToSourceId(sourceCode);

        var remoteId = await _remoteDb.UpsertOrderAsync(
            externalOrderId: externalOrderId,
            pickupStoreId: pickupStoreId,
            orderSourceId: orderSourceId,
            orderStatusId: reader.GetInt32(3),
            customerFirstName: reader.IsDBNull(4) ? null : reader.GetString(4),
            customerLastName: reader.IsDBNull(5) ? null : reader.GetString(5),
            customerEmail: reader.IsDBNull(6) ? null : reader.GetString(6),
            customerPhone: reader.IsDBNull(7) ? null : reader.GetString(7),
            totalAmount: reader.IsDBNull(8) ? null : (decimal?)reader.GetDouble(8),
            isHeld: reader.GetInt32(9) == 1,
            isTransfer: reader.GetInt32(10) == 1,
            transferStoreId: reader.IsDBNull(11) ? null : reader.GetInt32(11),
            specialInstructions: reader.IsDBNull(12) ? null : reader.GetString(12),
            folderPath: reader.IsDBNull(13) ? null : reader.GetString(13),
            deliveryMethodId: reader.GetInt32(14),
            orderedAt: reader.IsDBNull(15) ? null : reader.GetString(15),
            pixfizzJobId: reader.IsDBNull(16) ? null : reader.GetString(16));
        reader.Close();

        if (remoteId <= 0) return false;

        // Cache the id mapping
        _outbox.SetIdMapping("orders", localOrderId, remoteId);

        // Push items
        var items = ReadLocalItems(conn, localOrderId);
        AppLog.Info($"SyncPush: order {externalOrderId} (local={localOrderId}, remote={remoteId}), {items.Count} items to push");
        if (items.Count > 0)
        {
            var itemResult = await _remoteDb.UpsertOrderItemsAsync(remoteId, items);
            AppLog.Info($"SyncPush: items push result={itemResult} for order {externalOrderId}");
        }

        return true;
    }

    private List<(string SizeLabel, string MediaType, int Quantity, string ImageFilename, string ImageFilepath, string OriginalImageFilepath, string OptionsJson, bool IsPrinted)> ReadLocalItems(SqliteConnection conn, int orderId)
    {
        var items = new List<(string, string, int, string, string, string, string, bool)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT size_label, media_type, quantity, image_filename, image_filepath,
                   original_image_filepath, options_json, is_printed
            FROM order_items WHERE order_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", orderId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add((
                reader.IsDBNull(0) ? "" : reader.GetString(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3),
                reader.IsDBNull(4) ? "" : reader.GetString(4),
                reader.IsDBNull(5) ? "" : reader.GetString(5),
                reader.IsDBNull(6) ? "[]" : reader.GetString(6),
                reader.GetInt32(7) == 1));
        }
        return items;
    }

    // ── Pull ─────────────────────────────────────────────────────────────

    public async Task PullAsync()
    {
        try
        {
            var lastSync = _outbox.GetLastSyncAt("orders", "pull") ?? DateTime.MinValue;
            var pendingOrderIds = _outbox.GetPendingOrderIds();

            // Pull orders
            var remoteOrders = await _remoteDb.GetOrdersUpdatedSinceAsync(lastSync);
            if (remoteOrders.Count == 0)
            {
                _outbox.SetLastSyncAt("orders", "pull", DateTime.UtcNow);
                return;
            }

            var pulledMariaDbOrderIds = new List<int>();

            int orderErrors = 0;
            foreach (var row in remoteOrders)
            {
                try
                {
                    var externalOrderId = (string)row.external_order_id;
                    var pickupStoreId = (int)row.pickup_store_id;
                    var mariaDbOrderId = (int)row.id;

                    // Check if this order has pending outbox entries — skip if so
                    var localId = FindLocalOrderId(externalOrderId, pickupStoreId);
                    if (localId.HasValue && pendingOrderIds.Contains(localId.Value))
                        continue;

                    UpsertLocalOrder(row, localId);

                    // Update id_map
                    var newLocalId = FindLocalOrderId(externalOrderId, pickupStoreId);
                    if (newLocalId.HasValue)
                    {
                        _outbox.SetIdMapping("orders", newLocalId.Value, mariaDbOrderId);
                        pulledMariaDbOrderIds.Add(mariaDbOrderId);
                    }
                }
                catch (Exception ex)
                {
                    orderErrors++;
                    if (orderErrors <= 10)
                    {
                        var eid = "unknown";
                        try { eid = (string)row.external_order_id; } catch { }
                        AlertCollector.Error(AlertCategory.Database,
                            "Failed to pull order into SQLite",
                            detail: $"Attempted: upsert pulled order '{eid}'. " +
                                    $"Expected: local SQLite updated. " +
                                    $"Found: {ex.GetType().Name}: {ex.Message}. " +
                                    $"Context: sync pull. " +
                                    $"State: order skipped, will retry next cycle.",
                            ex: ex);
                    }
                    if (orderErrors == 10)
                    {
                        AlertCollector.Error(AlertCategory.Database,
                            "Sync pull: too many order errors, skipping remaining",
                            detail: $"Attempted: pull orders. Expected: < 10 errors. Found: 10+. " +
                                    $"Context: sync pull aborted for this cycle. State: will retry next cycle.");
                        break;
                    }
                }
            }

            // Also fetch items for orders that have 0 local items (missed on prior pull)
            AddOrdersMissingItems(pulledMariaDbOrderIds);

            // Pull items for orders we just upserted or that are missing items
            if (pulledMariaDbOrderIds.Count > 0)
            {
                var remoteItems = await _remoteDb.GetOrderItemsForOrdersAsync(pulledMariaDbOrderIds);
                int itemErrors = 0;
                foreach (var item in remoteItems)
                {
                    try
                    {
                        UpsertLocalItem(item);
                    }
                    catch (Exception ex)
                    {
                        itemErrors++;
                        if (itemErrors <= 10)
                        {
                            AlertCollector.Error(AlertCategory.Database,
                                "Failed to pull order item into SQLite",
                                detail: $"Attempted: upsert pulled item. " +
                                        $"Expected: local item updated. " +
                                        $"Found: {ex.GetType().Name}. " +
                                        $"Context: sync pull. " +
                                        $"State: item skipped.",
                                ex: ex);
                        }
                        if (itemErrors == 10)
                        {
                            AlertCollector.Error(AlertCategory.Database,
                                "Sync pull: too many item errors, skipping remaining",
                                detail: $"Attempted: pull items. Expected: < 10 errors. Found: 10+. " +
                                        $"Context: sync pull aborted for this cycle. State: will retry next cycle.");
                            break;
                        }
                    }
                }
            }

            // Pull notes
            var lastNotesSync = _outbox.GetLastSyncAt("order_notes", "pull") ?? DateTime.MinValue;
            var remoteNotes = await _remoteDb.GetOrderNotesSinceAsync(lastNotesSync);
            int noteErrors = 0;
            foreach (var note in remoteNotes)
            {
                try
                {
                    InsertRemoteNote(note);
                }
                catch (Exception ex)
                {
                    noteErrors++;
                    if (noteErrors <= 10)
                    {
                        AlertCollector.Error(AlertCategory.Database,
                            "Failed to pull note into SQLite",
                            detail: $"Attempted: insert pulled note. " +
                                    $"Expected: local history updated. " +
                                    $"Found: {ex.GetType().Name}. " +
                                    $"Context: sync pull. " +
                                    $"State: note skipped.",
                            ex: ex);
                    }
                    if (noteErrors == 10)
                    {
                        AlertCollector.Error(AlertCategory.Database,
                            "Sync pull: too many note errors, skipping remaining",
                            detail: $"Attempted: pull notes. Expected: < 10 errors. Found: 10+. " +
                                    $"Context: sync pull aborted for this cycle. State: will retry next cycle.");
                        break;
                    }
                }
            }

            _outbox.SetLastSyncAt("orders", "pull", DateTime.UtcNow);
            _outbox.SetLastSyncAt("order_notes", "pull", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "MariaDB pull failed",
                detail: $"Attempted: pull changes from MariaDB. " +
                        $"Expected: local SQLite updated. " +
                        $"Found: {ex.GetType().Name}: {ex.Message}. " +
                        $"Context: sync timer. " +
                        $"State: will retry next cycle.",
                ex: ex);
        }
    }

    private int? FindLocalOrderId(string externalOrderId, int pickupStoreId)
    {
        using var conn = _localDb.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM orders WHERE external_order_id = @eid AND pickup_store_id = @store";
        cmd.Parameters.AddWithValue("@eid", externalOrderId);
        cmd.Parameters.AddWithValue("@store", pickupStoreId);
        var result = cmd.ExecuteScalar();
        return result != null ? Convert.ToInt32(result) : null;
    }

    private void UpsertLocalOrder(dynamic row, int? existingLocalId)
    {
        using var conn = _localDb.OpenConnection();

        string externalOrderId = (string)row.external_order_id;
        int pickupStoreId = (int)row.pickup_store_id;
        string statusCode = (string)(row.StatusCode ?? "new");
        string sourceCode = (string)(row.SourceCode ?? "pixfizz");
        int orderStatusId = SyncMapper.StatusCodeToStatusId(statusCode);
        int orderSourceId = SyncMapper.SourceCodeToSourceId(sourceCode);

        if (existingLocalId.HasValue)
        {
            // Compare updated_at — only update if MariaDB is newer
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT updated_at FROM orders WHERE id = @id";
            checkCmd.Parameters.AddWithValue("@id", existingLocalId.Value);
            var localUpdatedStr = checkCmd.ExecuteScalar() as string;
            if (localUpdatedStr != null && DateTime.TryParse(localUpdatedStr, out var localUpdated))
            {
                DateTime remoteUpdated = (DateTime)row.updated_at;
                if (localUpdated >= remoteUpdated)
                    return; // local is newer or same — skip
            }

            // Update existing
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE orders SET
                    order_source_id = @srcId, source_code = @srcCode,
                    order_status_id = @statusId, status_code = @statusCode,
                    customer_first_name = @fname, customer_last_name = @lname,
                    customer_email = @email, customer_phone = @phone,
                    total_amount = @total, is_held = @held,
                    is_transfer = @transfer, transfer_store_id = @transferStore,
                    special_instructions = @instructions,
                    delivery_method_id = @delivery, ordered_at = @orderedAt,
                    updated_at = @updatedAt
                WHERE id = @id
                """;
            BindOrderParams(cmd, row, sourceCode, orderSourceId, statusCode, orderStatusId);
            cmd.Parameters.AddWithValue("@updatedAt", ((DateTime)row.updated_at).ToString("o"));
            cmd.Parameters.AddWithValue("@id", existingLocalId.Value);
            cmd.ExecuteNonQuery();
        }
        else
        {
            // Insert new
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO orders (
                    external_order_id, pickup_store_id, order_source_id, source_code,
                    order_status_id, status_code,
                    customer_first_name, customer_last_name, customer_email, customer_phone,
                    total_amount, is_held, is_transfer, transfer_store_id,
                    special_instructions, folder_path, delivery_method_id, ordered_at,
                    created_at, updated_at
                ) VALUES (
                    @eid, @store, @srcId, @srcCode,
                    @statusId, @statusCode,
                    @fname, @lname, @email, @phone,
                    @total, @held, @transfer, @transferStore,
                    @instructions, @folder, @delivery, @orderedAt,
                    @createdAt, @updatedAt
                )
                """;
            cmd.Parameters.AddWithValue("@eid", externalOrderId);
            cmd.Parameters.AddWithValue("@store", pickupStoreId);
            BindOrderParams(cmd, row, sourceCode, orderSourceId, statusCode, orderStatusId);
            cmd.Parameters.AddWithValue("@createdAt", ((DateTime)row.created_at).ToString("o"));
            cmd.Parameters.AddWithValue("@updatedAt", ((DateTime)row.updated_at).ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    private static void BindOrderParams(SqliteCommand cmd, dynamic row, string sourceCode, int orderSourceId, string statusCode, int orderStatusId)
    {
        cmd.Parameters.AddWithValue("@srcId", orderSourceId);
        cmd.Parameters.AddWithValue("@srcCode", sourceCode);
        cmd.Parameters.AddWithValue("@statusId", orderStatusId);
        cmd.Parameters.AddWithValue("@statusCode", statusCode);
        cmd.Parameters.AddWithValue("@fname", (string?)row.customer_first_name ?? "");
        cmd.Parameters.AddWithValue("@lname", (string?)row.customer_last_name ?? "");
        cmd.Parameters.AddWithValue("@email", (string?)row.customer_email ?? "");
        cmd.Parameters.AddWithValue("@phone", (string?)row.customer_phone ?? "");
        cmd.Parameters.AddWithValue("@total", row.total_amount != null ? (double)(decimal)row.total_amount : 0.0);
        cmd.Parameters.AddWithValue("@held", Convert.ToBoolean(row.is_held) ? 1 : 0);
        cmd.Parameters.AddWithValue("@transfer", Convert.ToBoolean(row.is_transfer) ? 1 : 0);
        cmd.Parameters.AddWithValue("@transferStore", row.transfer_store_id != null ? (object)(int)row.transfer_store_id : DBNull.Value);
        cmd.Parameters.AddWithValue("@instructions", (string?)row.special_instructions ?? "");
        cmd.Parameters.AddWithValue("@folder", (string?)row.folder_path ?? "");
        cmd.Parameters.AddWithValue("@delivery", row.delivery_method_id != null ? (int)row.delivery_method_id : 1);
        cmd.Parameters.AddWithValue("@orderedAt", row.ordered_at != null ? ((DateTime)row.ordered_at).ToString("o") : DateTime.Now.ToString("o"));
    }

    private void UpsertLocalItem(dynamic item)
    {
        int mariaDbOrderId = (int)item.order_id;
        var localOrderId = _outbox.GetLocalId("orders", mariaDbOrderId);
        if (!localOrderId.HasValue) return; // can't map this item

        string sizeLabel = (string?)item.size_label ?? "";
        string mediaType = (string?)item.media_type ?? "";
        string imageFilename = (string?)item.image_filename ?? "";

        using var conn = _localDb.OpenConnection();

        // Check if item exists by natural key
        using var findCmd = conn.CreateCommand();
        findCmd.CommandText = """
            SELECT id FROM order_items
            WHERE order_id = @oid AND image_filename = @fname
              AND size_label = @size AND media_type = @media
            """;
        findCmd.Parameters.AddWithValue("@oid", localOrderId.Value);
        findCmd.Parameters.AddWithValue("@fname", imageFilename);
        findCmd.Parameters.AddWithValue("@size", sizeLabel);
        findCmd.Parameters.AddWithValue("@media", mediaType);
        var existingId = findCmd.ExecuteScalar();

        if (existingId != null)
        {
            // Update existing item
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE order_items SET
                    quantity = @qty, image_filepath = @fpath,
                    options_json = @options,
                    is_printed = @printed, updated_at = datetime('now')
                WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("@qty", (int)item.quantity);
            cmd.Parameters.AddWithValue("@fpath", (string?)item.image_filepath ?? "");
            cmd.Parameters.AddWithValue("@options", (string?)item.options_json ?? "[]");
            cmd.Parameters.AddWithValue("@printed", Convert.ToBoolean(item.is_printed ?? false) ? 1 : 0);
            cmd.Parameters.AddWithValue("@id", Convert.ToInt32(existingId));
            cmd.ExecuteNonQuery();
        }
        else
        {
            // Insert new item
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO order_items (
                    order_id, size_label, media_type, quantity,
                    image_filename, image_filepath,
                    options_json, is_printed
                ) VALUES (
                    @oid, @size, @media, @qty,
                    @fname, @fpath,
                    @options, @printed
                )
                """;
            cmd.Parameters.AddWithValue("@oid", localOrderId.Value);
            cmd.Parameters.AddWithValue("@size", sizeLabel);
            cmd.Parameters.AddWithValue("@media", mediaType);
            cmd.Parameters.AddWithValue("@qty", (int)item.quantity);
            cmd.Parameters.AddWithValue("@fname", imageFilename);
            cmd.Parameters.AddWithValue("@fpath", (string?)item.image_filepath ?? "");
            cmd.Parameters.AddWithValue("@options", (string?)item.options_json ?? "[]");
            cmd.Parameters.AddWithValue("@printed", Convert.ToBoolean(item.is_printed ?? false) ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    private void InsertRemoteNote(dynamic note)
    {
        int mariaDbNoteId = (int)note.id;
        int mariaDbOrderId = (int)note.order_id;
        var localOrderId = _outbox.GetLocalId("orders", mariaDbOrderId);
        if (!localOrderId.HasValue) return;

        using var conn = _localDb.OpenConnection();

        // Verify the local order still exists (id_map can outlive deleted orders)
        using var existsCmd = conn.CreateCommand();
        existsCmd.CommandText = "SELECT COUNT(*) FROM orders WHERE id = @id";
        existsCmd.Parameters.AddWithValue("@id", localOrderId.Value);
        if (Convert.ToInt32(existsCmd.ExecuteScalar()) == 0) return;

        // Check if we already have this remote note
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM order_history WHERE remote_id = @rid";
        checkCmd.Parameters.AddWithValue("@rid", mariaDbNoteId);
        var count = Convert.ToInt32(checkCmd.ExecuteScalar());
        if (count > 0) return; // already pulled

        string noteText = (string)note.note_text;
        string employeeName = ((string?)note.EmployeeName ?? "").Trim();
        string createdAt = ((DateTime)note.created_at).ToString("o");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO order_history (order_id, note, created_by, created_at, remote_id)
            VALUES (@oid, @note, @by, @at, @rid)
            """;
        cmd.Parameters.AddWithValue("@oid", localOrderId.Value);
        cmd.Parameters.AddWithValue("@note", noteText);
        cmd.Parameters.AddWithValue("@by", employeeName);
        cmd.Parameters.AddWithValue("@at", createdAt);
        cmd.Parameters.AddWithValue("@rid", mariaDbNoteId);
        cmd.ExecuteNonQuery();
    }

    // ── Outbox retry ─────────────────────────────────────────────────────

    public async Task ProcessOutboxAsync()
    {
        try
        {
            var pending = _outbox.GetPending();
            foreach (var entry in pending)
            {
                try
                {
                    var success = await ExecutePushAsync(entry.Operation, entry.PayloadJson);
                    if (success)
                        _outbox.MarkPushed(entry.Id);
                }
                catch (Exception ex)
                {
                    AlertCollector.Error(AlertCategory.Database,
                        $"Outbox retry failed: {entry.Operation}",
                        detail: $"Attempted: retry {entry.Operation} for {entry.TableName} record {entry.RecordId}. " +
                                $"Expected: MariaDB updated. " +
                                $"Found: {ex.GetType().Name}: {ex.Message}. " +
                                $"Context: outbox retry (entry {entry.Id}, created {entry.CreatedAt}). " +
                                $"State: will retry next cycle.",
                        ex: ex);
                }
            }

            _outbox.PurgePushed();
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Outbox processing failed",
                detail: $"Attempted: process sync outbox. " +
                        $"Expected: pending entries retried. " +
                        $"Found: {ex.GetType().Name}: {ex.Message}. " +
                        $"Context: sync timer. " +
                        $"State: will retry next cycle.",
                ex: ex);
        }
    }

    // ── Reachability ─────────────────────────────────────────────────────

    public async Task<bool> IsReachableAsync()
    {
        var error = await _remoteDb.TestConnectionAsync();
        return error == null;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private bool IsOurOrder(int localOrderId)
    {
        using var conn = _localDb.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pickup_store_id FROM orders WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", localOrderId);
        var result = cmd.ExecuteScalar();
        if (result == null) return false;
        return Convert.ToInt32(result) == _settings.StoreId;
    }

    private async Task<int?> ResolveRemoteOrderIdAsync(int localOrderId)
    {
        // Check cache first
        var cached = _outbox.GetRemoteId("orders", localOrderId);
        if (cached.HasValue) return cached.Value;

        // Look up by natural key
        using var conn = _localDb.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT external_order_id, pickup_store_id FROM orders WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", localOrderId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var eid = reader.GetString(0);
        var store = reader.GetInt32(1);
        reader.Close();

        var remoteId = await _remoteDb.FindOrderIdByNaturalKeyAsync(eid, store);
        if (remoteId.HasValue)
            _outbox.SetIdMapping("orders", localOrderId, remoteId.Value);

        return remoteId;
    }

    private void AddOrdersMissingItems(List<int> mariaDbOrderIds)
    {
        using var conn = _localDb.OpenConnection();
        using var cmd = conn.CreateCommand();
        // Find orders that have an id_map entry but 0 local items
        cmd.CommandText = """
            SELECT m.remote_id FROM id_map m
            WHERE m.table_name = 'orders'
              AND NOT EXISTS (
                  SELECT 1 FROM order_items i WHERE i.order_id = m.local_id
              )
            LIMIT 100
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var remoteId = reader.GetInt32(0);
            if (!mariaDbOrderIds.Contains(remoteId))
                mariaDbOrderIds.Add(remoteId);
        }
    }

    private static int GetOrderIdFromPayload(string payloadJson)
    {
        try
        {
            var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("orderId", out var oid))
                return oid.GetInt32();
            if (doc.RootElement.TryGetProperty("localOrderId", out var lid))
                return lid.GetInt32();
        }
        catch { }
        return 0;
    }
}
