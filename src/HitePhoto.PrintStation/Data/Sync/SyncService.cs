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
                return await _remoteDb.UpdateOrderStatusAsync(remoteId.Value, SyncMapper.StatusCodeToStatusId("notified"));
            }

            case "set_items_printed":
            {
                var orderId = payload["orderId"].GetInt32();
                var itemIds = payload["itemIds"].Deserialize<List<int>>()!;
                var remoteId = await ResolveRemoteOrderIdAsync(orderId);
                if (remoteId == null) return false;
                var failures = 0;
                foreach (var localItemId in itemIds)
                {
                    var ok = await _remoteDb.UpdateItemPrintedAsync(localItemId, DateTime.Now);
                    if (!ok) failures++;
                }
                if (failures > 0)
                    AlertCollector.Error(AlertCategory.Database,
                        $"set_items_printed: {failures}/{itemIds.Count} items failed",
                        detail: $"Attempted: mark {itemIds.Count} items printed on MariaDB order {remoteId}. " +
                                $"Expected: all items updated. Found: {failures} failures. " +
                                $"Context: sync push. State: partial update");
                return failures == 0;
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
                AlertCollector.Error(AlertCategory.Database,
                    $"Unknown sync push operation '{operation}'",
                    detail: $"Attempted: process outbox entry. Expected: known operation (upsert_order, add_note). " +
                            $"Found: '{operation}'. Context: sync push. State: entry skipped.");
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
                   ordered_at, pixfizz_job_id, source_code,
                   is_rush, payment_status, is_notified, notified_at,
                   shipping_first_name, shipping_last_name,
                   shipping_address1, shipping_address2, shipping_city,
                   shipping_state, shipping_zip, shipping_country, shipping_method
            FROM orders WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", localOrderId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return false;

        var externalOrderId = reader.GetString(0);
        var pickupStoreId = reader.GetInt32(1);
        var sourceCode = reader.IsDBNull(17) ? "pixfizz" : reader.GetString(17);
        var orderSourceId = SyncMapper.SourceCodeToSourceId(sourceCode);

        string? ReadNullableString(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        int? ReadNullableInt(int i) => reader.IsDBNull(i) ? null : reader.GetInt32(i);

        var remoteId = await _remoteDb.UpsertOrderAsync(
            externalOrderId: externalOrderId,
            pickupStoreId: pickupStoreId,
            orderSourceId: orderSourceId,
            orderStatusId: reader.GetInt32(3),
            customerFirstName: ReadNullableString(4),
            customerLastName: ReadNullableString(5),
            customerEmail: ReadNullableString(6),
            customerPhone: ReadNullableString(7),
            totalAmount: reader.IsDBNull(8) ? null : (decimal?)reader.GetDouble(8),
            isHeld: reader.GetInt32(9) == 1,
            isTransfer: reader.GetInt32(10) == 1,
            transferStoreId: ReadNullableInt(11),
            specialInstructions: ReadNullableString(12),
            folderPath: ReadNullableString(13),
            deliveryMethodId: reader.GetInt32(14),
            orderedAt: ReadNullableString(15),
            pixfizzJobId: ReadNullableString(16),
            isRush: reader.GetInt32(18) == 1,
            paymentStatus: ReadNullableString(19),
            isNotified: reader.GetInt32(20) == 1,
            notifiedAt: ReadNullableString(21),
            shippingFirstName: ReadNullableString(22),
            shippingLastName: ReadNullableString(23),
            shippingAddress1: ReadNullableString(24),
            shippingAddress2: ReadNullableString(25),
            shippingCity: ReadNullableString(26),
            shippingState: ReadNullableString(27),
            shippingZip: ReadNullableString(28),
            shippingCountry: ReadNullableString(29),
            shippingMethod: ReadNullableString(30));
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

            foreach (var row in remoteOrders)
            {
                try
                {
                    object eidVal = row.external_order_id;
                    var externalOrderId = eidVal is null or DBNull
                        ? throw new InvalidOperationException($"Pulled order id={row.id} has NULL external_order_id")
                        : eidVal.ToString()!;
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
                    object eidFallback = row.external_order_id;
                    var eid = eidFallback is null or DBNull ? "unknown" : eidFallback.ToString()!;
                    AlertCollector.Error(AlertCategory.Database,
                        "Failed to pull order into SQLite",
                        detail: $"Attempted: upsert pulled order '{eid}'. " +
                                $"Expected: local SQLite updated. " +
                                $"Found: {ex.GetType().Name}: {ex.Message}. " +
                                $"Context: sync pull. " +
                                $"State: order skipped, will retry next cycle.",
                        ex: ex);
                }
            }

            // Also fetch items for orders that have 0 local items (missed on prior pull)
            AddOrdersMissingItems(pulledMariaDbOrderIds);

            // Pull items for orders we just upserted or that are missing items
            if (pulledMariaDbOrderIds.Count > 0)
            {
                var remoteItems = await _remoteDb.GetOrderItemsForOrdersAsync(pulledMariaDbOrderIds);
                foreach (var item in remoteItems)
                {
                    try
                    {
                        UpsertLocalItem(item);
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
                    {
                        // Foreign key constraint — parent order doesn't exist locally. Expected after wipe.
                        AppLog.Info($"Sync pull: skipped item (parent order missing locally)");
                    }
                    catch (Exception ex)
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
                }
            }

            // Pull notes
            var lastNotesSync = _outbox.GetLastSyncAt("order_notes", "pull") ?? DateTime.MinValue;
            var remoteNotes = await _remoteDb.GetOrderNotesSinceAsync(lastNotesSync);
            foreach (var note in remoteNotes)
            {
                try
                {
                    InsertRemoteNote(note);
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    AppLog.Info($"Sync pull: skipped note (parent order missing locally)");
                }
                catch (Exception ex)
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

        object eidObj = row.external_order_id;
        string externalOrderId = eidObj is null or DBNull
            ? throw new InvalidOperationException("Pulled order has NULL external_order_id")
            : eidObj.ToString()!;
        int pickupStoreId = (int)row.pickup_store_id;
        object scObj = row.StatusCode;
        string statusCode = scObj is null or DBNull ? "new" : scObj.ToString()!;
        object srcObj = row.SourceCode;
        string sourceCode = srcObj is null or DBNull ? "pixfizz" : srcObj.ToString()!;
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
                    total_amount = @total, payment_status = @paymentStatus,
                    is_held = @held, is_notified = @notified, notified_at = @notifiedAt,
                    is_transfer = @transfer, transfer_store_id = @transferStore,
                    special_instructions = @instructions, folder_path = @folder,
                    delivery_method_id = @delivery, ordered_at = @orderedAt,
                    pixfizz_job_id = @jobId, is_rush = @rush,
                    current_location_store_id = @currentStore,
                    shipping_first_name = @shipFname, shipping_last_name = @shipLname,
                    shipping_address1 = @shipAddr1, shipping_address2 = @shipAddr2,
                    shipping_city = @shipCity, shipping_state = @shipState,
                    shipping_zip = @shipZip, shipping_country = @shipCountry,
                    shipping_method = @shipMethod,
                    is_test = @isTest, updated_at = @updatedAt
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
                    total_amount, payment_status,
                    is_held, is_notified, notified_at,
                    is_transfer, transfer_store_id,
                    special_instructions, folder_path, delivery_method_id, ordered_at,
                    pixfizz_job_id, is_rush, current_location_store_id,
                    shipping_first_name, shipping_last_name,
                    shipping_address1, shipping_address2, shipping_city,
                    shipping_state, shipping_zip, shipping_country, shipping_method,
                    is_test, created_at, updated_at
                ) VALUES (
                    @eid, @store, @srcId, @srcCode,
                    @statusId, @statusCode,
                    @fname, @lname, @email, @phone,
                    @total, @paymentStatus,
                    @held, @notified, @notifiedAt,
                    @transfer, @transferStore,
                    @instructions, @folder, @delivery, @orderedAt,
                    @jobId, @rush, @currentStore,
                    @shipFname, @shipLname,
                    @shipAddr1, @shipAddr2, @shipCity,
                    @shipState, @shipZip, @shipCountry, @shipMethod,
                    @isTest, @createdAt, @updatedAt
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
        // Helper to safely read nullable values from dynamic Dapper row.
        // Cannot use ?., ??, or != null on dynamic — they call HasValue which fails on value types.
        static object NStr(dynamic val)
        {
            object o = val;
            return o is null or DBNull ? DBNull.Value : o.ToString()!;
        }
        static object NInt(dynamic val)
        {
            object o = val;
            return o is null or DBNull ? DBNull.Value : (object)Convert.ToInt32(o);
        }
        static object NDateTime(dynamic val)
        {
            object o = val;
            return o is null or DBNull ? DBNull.Value : (object)((DateTime)o).ToString("o");
        }

        cmd.Parameters.AddWithValue("@srcId", orderSourceId);
        cmd.Parameters.AddWithValue("@srcCode", sourceCode);
        cmd.Parameters.AddWithValue("@statusId", orderStatusId);
        cmd.Parameters.AddWithValue("@statusCode", statusCode);
        cmd.Parameters.AddWithValue("@fname", NStr(row.customer_first_name));
        cmd.Parameters.AddWithValue("@lname", NStr(row.customer_last_name));
        cmd.Parameters.AddWithValue("@email", NStr(row.customer_email));
        cmd.Parameters.AddWithValue("@phone", NStr(row.customer_phone));
        cmd.Parameters.AddWithValue("@total", row.total_amount != null ? (double)(decimal)row.total_amount : 0.0);
        cmd.Parameters.AddWithValue("@paymentStatus", NStr(row.payment_status));
        cmd.Parameters.AddWithValue("@held", Convert.ToBoolean(row.is_held) ? 1 : 0);
        cmd.Parameters.AddWithValue("@notified", Convert.ToInt32(row.is_notified) != 0 ? 1 : 0);
        cmd.Parameters.AddWithValue("@notifiedAt", NDateTime(row.notified_at));
        cmd.Parameters.AddWithValue("@transfer", Convert.ToBoolean(row.is_transfer) ? 1 : 0);
        cmd.Parameters.AddWithValue("@transferStore", NInt(row.transfer_store_id));
        cmd.Parameters.AddWithValue("@instructions", NStr(row.special_instructions));
        cmd.Parameters.AddWithValue("@folder", NStr(row.folder_path));
        cmd.Parameters.AddWithValue("@delivery", row.delivery_method_id != null ? (int)row.delivery_method_id : 1);
        cmd.Parameters.AddWithValue("@orderedAt", row.ordered_at != null ? ((DateTime)row.ordered_at).ToString("o") : DateTime.Now.ToString("o"));
        cmd.Parameters.AddWithValue("@jobId", NStr(row.pixfizz_job_id));
        cmd.Parameters.AddWithValue("@rush", Convert.ToInt32(row.is_rush) != 0 ? 1 : 0);
        cmd.Parameters.AddWithValue("@currentStore", NInt(row.current_location_store_id));
        cmd.Parameters.AddWithValue("@shipFname", NStr(row.shipping_first_name));
        cmd.Parameters.AddWithValue("@shipLname", NStr(row.shipping_last_name));
        cmd.Parameters.AddWithValue("@shipAddr1", NStr(row.shipping_address1));
        cmd.Parameters.AddWithValue("@shipAddr2", NStr(row.shipping_address2));
        cmd.Parameters.AddWithValue("@shipCity", NStr(row.shipping_city));
        cmd.Parameters.AddWithValue("@shipState", NStr(row.shipping_state));
        cmd.Parameters.AddWithValue("@shipZip", NStr(row.shipping_zip));
        cmd.Parameters.AddWithValue("@shipCountry", NStr(row.shipping_country));
        cmd.Parameters.AddWithValue("@shipMethod", NStr(row.shipping_method));
        cmd.Parameters.AddWithValue("@isTest", Convert.ToInt32(row.is_test) != 0 ? 1 : 0);
    }

    private void UpsertLocalItem(dynamic item)
    {
        int mariaDbOrderId = (int)item.order_id;
        var localOrderId = _outbox.GetLocalId("orders", mariaDbOrderId);
        if (!localOrderId.HasValue) return; // can't map this item

        object slObj = item.size_label;
        string sizeLabel = slObj is null or DBNull ? "" : slObj.ToString()!;
        object mtObj = item.media_type;
        string mediaType = mtObj is null or DBNull ? "" : mtObj.ToString()!;
        object fnObj = item.image_filename;
        string imageFilename = fnObj is null or DBNull ? "" : fnObj.ToString()!;

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

        object origObj = item.original_image_filepath;
        string origFilepath = origObj is null or DBNull ? "" : origObj.ToString()!;
        bool isPrinted = Convert.ToInt32(item.is_printed) != 0;

        if (existingId != null)
        {
            // Update existing item
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE order_items SET
                    quantity = @qty, image_filepath = @fpath,
                    original_image_filepath = @orig,
                    options_json = @options,
                    is_printed = @printed, updated_at = datetime('now')
                WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("@qty", (int)item.quantity);
            cmd.Parameters.AddWithValue("@fpath", ((object)item.image_filepath is null or DBNull ? "" : ((object)item.image_filepath).ToString()!));
            cmd.Parameters.AddWithValue("@orig", origFilepath);
            cmd.Parameters.AddWithValue("@options", ((object)item.options_json is null or DBNull ? "[]" : ((object)item.options_json).ToString()!));
            cmd.Parameters.AddWithValue("@printed", isPrinted ? 1 : 0);
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
                    image_filename, image_filepath, original_image_filepath,
                    options_json, is_printed
                ) VALUES (
                    @oid, @size, @media, @qty,
                    @fname, @fpath, @orig,
                    @options, @printed
                )
                """;
            cmd.Parameters.AddWithValue("@oid", localOrderId.Value);
            cmd.Parameters.AddWithValue("@size", sizeLabel);
            cmd.Parameters.AddWithValue("@media", mediaType);
            cmd.Parameters.AddWithValue("@qty", (int)item.quantity);
            cmd.Parameters.AddWithValue("@fname", imageFilename);
            cmd.Parameters.AddWithValue("@fpath", ((object)item.image_filepath is null or DBNull ? "" : ((object)item.image_filepath).ToString()!));
            cmd.Parameters.AddWithValue("@orig", origFilepath);
            cmd.Parameters.AddWithValue("@options", ((object)item.options_json is null or DBNull ? "[]" : ((object)item.options_json).ToString()!));
            cmd.Parameters.AddWithValue("@printed", isPrinted ? 1 : 0);
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

        object ntObj = note.note_text;
        string noteText = ntObj is null or DBNull
            ? throw new InvalidOperationException($"Pulled note {mariaDbNoteId} has NULL note_text")
            : ntObj.ToString()!;
        object enObj = note.EmployeeName;
        string employeeName = (enObj is null or DBNull ? "" : enObj.ToString()!).Trim();
        string createdAt = ((DateTime)note.created_at).ToString("o");

        // Check if we already have this remote note by remote_id
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM order_history WHERE remote_id = @rid";
        checkCmd.Parameters.AddWithValue("@rid", mariaDbNoteId);
        var count = Convert.ToInt32(checkCmd.ExecuteScalar());
        if (count > 0) return; // already pulled

        // Also check if a locally-created note matches (same order, same text) — don't duplicate
        using var dupeCmd = conn.CreateCommand();
        dupeCmd.CommandText = """
            SELECT id FROM order_history
            WHERE order_id = @oid AND note = @note AND remote_id IS NULL
            LIMIT 1
            """;
        dupeCmd.Parameters.AddWithValue("@oid", localOrderId.Value);
        dupeCmd.Parameters.AddWithValue("@note", noteText);
        var localMatch = dupeCmd.ExecuteScalar();
        if (localMatch != null)
        {
            // Tag the existing local note with the remote_id so we don't check again
            using var tagCmd = conn.CreateCommand();
            tagCmd.CommandText = "UPDATE order_history SET remote_id = @rid WHERE id = @id";
            tagCmd.Parameters.AddWithValue("@rid", mariaDbNoteId);
            tagCmd.Parameters.AddWithValue("@id", Convert.ToInt32(localMatch));
            tagCmd.ExecuteNonQuery();
            return;
        }

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
