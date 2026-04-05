using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using HitePhoto.PrintStation.Core;

namespace HitePhoto.PrintStation.Data.Sync;

public record LocalOrderItem(
    string SizeLabel, string MediaType, int Quantity,
    string ImageFilename, string ImageFilepath, string OriginalImageFilepath,
    string OptionsJson, bool IsPrinted,
    int? FulfillmentStoreId, string? SourceItemId, int? ImageWidth, int? ImageHeight);

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

    public async Task<bool> PushAsync(string tableName, string recordId, string operation, string payloadJson)
    {
        try
        {
            // Only the pickup store pushes new order inserts (prevents duplicates).
            // All other operations (hold, status, notes, printed) push from any store.
            if (operation == "insert_order")
            {
                var orderId = GetOrderIdFromPayload(payloadJson);
                if (!string.IsNullOrEmpty(orderId) && !IsOurOrder(orderId))
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
                return await PushInsertOrderAsync(payload) != null;

            case "set_hold":
            {
                var orderId = payload["orderId"].GetString()!;
                if (VerifyOrderExists(orderId) == null) return false;
                var isHeld = payload["isHeld"].GetBoolean();
                return await _remoteDb.ToggleHoldAsync(orderId, isHeld);
            }

            case "set_display_tab":
            {
                var orderId = payload["orderId"].GetString()!;
                var mariaDbId = await EnsureOrderInMariaDbAsync(orderId);
                if (mariaDbId == null) return false;
                var displayTab = payload["displayTab"].GetInt32();
                return await _remoteDb.SetDisplayTabAsync(mariaDbId, displayTab);
            }

            case "set_printed":
            {
                var orderId = payload["orderId"].GetString()!;
                var mariaDbId = await EnsureOrderInMariaDbAsync(orderId);
                if (mariaDbId == null) return false;
                var printed = payload["printed"].GetBoolean();
                return await _remoteDb.SetOrderPrintedAsync(mariaDbId, printed);
            }

            case "update_status":
            {
                var orderId = payload["orderId"].GetString()!;
                if (VerifyOrderExists(orderId) == null) return false;
                var statusCode = payload["statusCode"].GetString()!;
                var statusId = SyncMapper.StatusCodeToStatusId(statusCode);
                return await _remoteDb.UpdateOrderStatusAsync(orderId, statusId);
            }

            case "set_notified":
            {
                var orderId = payload["orderId"].GetString()!;
                if (VerifyOrderExists(orderId) == null) return false;
                return await _remoteDb.UpdateOrderStatusAsync(orderId, 5); // notified
            }

            case "set_items_printed":
            {
                var orderId = payload["orderId"].GetString()!;
                if (VerifyOrderExists(orderId) == null) return false;
                var mariaDbId = await EnsureOrderInMariaDbAsync(orderId);
                if (mariaDbId == null) return false;
                using var conn = _localDb.OpenConnection();
                var items = ReadLocalItems(conn, orderId);
                if (items.Count == 0) return true;
                return await _remoteDb.UpsertOrderItemsAsync(mariaDbId, items);
            }

            case "set_current_location":
            {
                // No direct MariaDB method for this — covered by order upsert
                return true;
            }

            case "add_note":
            {
                var orderId = payload["orderId"].GetString()!;
                if (VerifyOrderExists(orderId) == null) return false;
                var note = payload["note"].GetString()!;
                var mariaDbNoteId = await _remoteDb.AddNoteAsync(orderId, null, note);
                if (string.IsNullOrEmpty(mariaDbNoteId)) return false;

                // Set remote_id on the local note so sync pull won't duplicate it.
                // This is best-effort — if it fails, the note is already in MariaDB
                // and we must return true to prevent re-pushing.
                try
                {
                    using var conn = _localDb.OpenConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        UPDATE order_history SET remote_id = @rid
                        WHERE id = (
                            SELECT id FROM order_history
                            WHERE order_id = @oid AND note = @note AND remote_id IS NULL
                            ORDER BY id DESC LIMIT 1
                        )
                        """;
                    cmd.Parameters.AddWithValue("@rid", mariaDbNoteId);
                    cmd.Parameters.AddWithValue("@oid", orderId);
                    cmd.Parameters.AddWithValue("@note", note);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    AppLog.Info($"SyncPush: failed to set remote_id on local note: {ex.Message}");
                }
                return true;
            }

            case "create_alteration":
                return await PushCreateAlterationAsync(payload);

            case "insert_link":
            {
                var parentId = payload["parentOrderId"].GetString()!;
                var childId = payload["childOrderId"].GetString()!;
                var linkType = payload["linkType"].GetString()!;
                var createdBy = payload.TryGetValue("createdBy", out var cb) ? cb.GetString() ?? "" : "";
                var mariaParent = await EnsureOrderInMariaDbAsync(parentId);
                if (mariaParent == null) return false;
                var mariaChild = await EnsureOrderInMariaDbAsync(childId);
                if (mariaChild == null) return false;
                return await _remoteDb.InsertOrderLinkAsync(mariaParent, mariaChild, linkType, createdBy);
            }

            default:
                AppLog.Info($"SyncService: unknown push operation '{operation}'");
                return true; // don't re-queue unknown ops
        }
    }

    /// <summary>
    /// Push a local order to MariaDB. Returns the MariaDB order ID (may differ
    /// from the local GUID if the other store pushed first), or null on failure.
    /// </summary>
    private async Task<string?> PushInsertOrderAsync(Dictionary<string, JsonElement> payload)
    {
        var localOrderId = payload["localOrderId"].GetString()!;

        using var conn = _localDb.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT external_order_id, pickup_store_id, order_source_id, order_status_id,
                   customer_first_name, customer_last_name, customer_email, customer_phone,
                   total_amount, is_held, is_transfer, transfer_store_id,
                   special_instructions, folder_path, delivery_method_id,
                   ordered_at, pixfizz_job_id, download_status, source_code,
                   harvested_by_store_id, is_printed, display_tab
            FROM orders WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", localOrderId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var externalOrderId = reader.GetString(0);
        var pickupStoreId = reader.GetInt32(1);
        var sourceCode = reader.IsDBNull(18) ? "pixfizz" : reader.GetString(18);
        var orderSourceId = SyncMapper.SourceCodeToSourceId(sourceCode);

        var mariaDbId = await _remoteDb.UpsertOrderAsync(
            orderId: localOrderId,
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
            pixfizzJobId: reader.IsDBNull(16) ? null : reader.GetString(16),
            harvestedByStoreId: reader.GetInt32(19),
            isPrinted: reader.GetInt32(20) == 1,
            displayTab: reader.GetInt32(21));
        reader.Close();

        if (string.IsNullOrEmpty(mariaDbId)) return null;

        var items = ReadLocalItems(conn, localOrderId);
        AppLog.Info($"SyncPush: order {externalOrderId} (local={localOrderId}, mariadb={mariaDbId}), {items.Count} items to push");
        if (items.Count > 0)
        {
            var itemResult = await _remoteDb.UpsertOrderItemsAsync(mariaDbId, items);
            AppLog.Info($"SyncPush: items push result={itemResult} for order {externalOrderId}");
            if (!itemResult) return null;
        }

        return mariaDbId;
    }

    private async Task<bool> PushCreateAlterationAsync(Dictionary<string, JsonElement> payload)
    {
        var localChildId = payload["localOrderId"].GetString()!;
        var localParentId = payload["sourceOrderId"].GetString()!;
        var alterationType = payload["alterationType"].GetString() ?? "split";

        // Push child order and get its MariaDB ID
        var mariaChildId = await PushInsertOrderAsync(
            new Dictionary<string, JsonElement>
            {
                ["localOrderId"] = JsonSerializer.SerializeToElement(localChildId)
            });
        if (mariaChildId == null)
        {
            AppLog.Info($"SyncPush: create_alteration failed — could not push child order {localChildId}");
            return false;
        }

        // Ensure parent exists in MariaDB and get its ID
        var mariaParentId = await EnsureOrderInMariaDbAsync(localParentId);
        if (mariaParentId == null) return false;

        // Sync parent's is_printed state
        if (VerifyOrderExists(localParentId) != null)
        {
            using var pConn = _localDb.OpenConnection();
            using var pCmd = pConn.CreateCommand();
            pCmd.CommandText = "SELECT is_printed FROM orders WHERE id = @id";
            pCmd.Parameters.AddWithValue("@id", localParentId);
            var parentPrinted = Convert.ToInt32(pCmd.ExecuteScalar()) == 1;
            if (!await _remoteDb.SetOrderPrintedAsync(mariaParentId, parentPrinted))
                return false;
        }

        // Insert order_links row using MariaDB IDs
        if (!await _remoteDb.InsertOrderLinkAsync(mariaParentId, mariaChildId, alterationType, ""))
            return false;

        AppLog.Info($"SyncPush: create_alteration complete — child {localChildId}→{mariaChildId}, parent {localParentId}→{mariaParentId}");
        return true;
    }

    private List<LocalOrderItem> ReadLocalItems(SqliteConnection conn, string orderId)
    {
        var items = new List<LocalOrderItem>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT size_label, media_type, quantity, image_filename, image_filepath,
                   original_image_filepath, options_json, is_printed,
                   fulfillment_store_id, source_item_id, image_width, image_height
            FROM order_items WHERE order_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", orderId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new LocalOrderItem(
                SizeLabel: reader.IsDBNull(0) ? "" : reader.GetString(0),
                MediaType: reader.IsDBNull(1) ? "" : reader.GetString(1),
                Quantity: reader.GetInt32(2),
                ImageFilename: reader.IsDBNull(3) ? "" : reader.GetString(3),
                ImageFilepath: reader.IsDBNull(4) ? "" : reader.GetString(4),
                OriginalImageFilepath: reader.IsDBNull(5) ? "" : reader.GetString(5),
                OptionsJson: reader.IsDBNull(6) ? "[]" : reader.GetString(6),
                IsPrinted: reader.GetInt32(7) == 1,
                FulfillmentStoreId: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                SourceItemId: reader.IsDBNull(9) ? null : reader.GetString(9),
                ImageWidth: reader.IsDBNull(10) ? null : reader.GetInt32(10),
                ImageHeight: reader.IsDBNull(11) ? null : reader.GetInt32(11)));
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

            // Pull orders (MariaDB uses server local time, not UTC)
            var remoteOrders = await _remoteDb.GetOrdersUpdatedSinceAsync(lastSync);
            if (remoteOrders.Count == 0)
            {
                _outbox.SetLastSyncAt("orders", "pull", DateTime.Now);
                return;
            }

            var pulledMariaDbOrderIds = new List<string>();

            int orderErrors = 0;
            foreach (var row in remoteOrders)
            {
                try
                {
                    var externalOrderId = (string)row.external_order_id;
                    var pickupStoreId = (int)row.pickup_store_id;
                    var mariaDbOrderId = Convert.ToString(row.id)!;

                    if (pendingOrderIds.Contains(mariaDbOrderId))
                        continue;

                    // Check if order already exists locally
                    var existingLocalId = FindLocalOrderId(externalOrderId, pickupStoreId);
                    UpsertLocalOrder(row, existingLocalId);
                    pulledMariaDbOrderIds.Add(mariaDbOrderId);
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

            // One-time: purge system junk from MariaDB so it stops syncing back
            await PurgeMariaDbNotesOnceAsync();

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

            // Pull order_links
            var lastLinksSync = _outbox.GetLastSyncAt("order_links", "pull") ?? DateTime.MinValue;
            var remoteLinks = await _remoteDb.GetOrderLinksSinceAsync(lastLinksSync);
            foreach (var link in remoteLinks)
            {
                try
                {
                    InsertRemoteLink(link);
                }
                catch (Exception ex)
                {
                    AlertCollector.Error(AlertCategory.Database,
                        "Failed to pull order link into SQLite",
                        detail: $"Attempted: insert pulled link. " +
                                $"Expected: local order_links updated. " +
                                $"Found: {ex.GetType().Name}. " +
                                $"Context: sync pull. " +
                                $"State: link skipped.",
                        ex: ex);
                }
            }

            _outbox.SetLastSyncAt("orders", "pull", DateTime.Now);
            _outbox.SetLastSyncAt("order_notes", "pull", DateTime.Now);
            _outbox.SetLastSyncAt("order_links", "pull", DateTime.Now);
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

    private string? FindLocalOrderId(string externalOrderId, int pickupStoreId)
    {
        using var conn = _localDb.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM orders WHERE external_order_id = @eid AND pickup_store_id = @store";
        cmd.Parameters.AddWithValue("@eid", externalOrderId);
        cmd.Parameters.AddWithValue("@store", pickupStoreId);
        var result = cmd.ExecuteScalar();
        return result != null ? Convert.ToString(result) : null;
    }

    // ═══════════════════════════════════════════════════════════════
    //  SYNC PULL — FIELD OWNERSHIP RULES
    //
    //  Pull UPDATE writes ONLY is_held + updated_at. Nothing else.
    //  MariaDB is a relay, not an authority. Edits create child
    //  orders linked via order_links — they never modify the parent.
    //
    //  Pull INSERT sets ALL fields (new orders from another store).
    //  BindPullInsertParams is standalone — not shared with UPDATE.
    //
    //  See: project_alteration_system.md, project_sync_authority.md
    // ═══════════════════════════════════════════════════════════════

    private void UpsertLocalOrder(dynamic row, string? existingLocalId)
    {
        using var conn = _localDb.OpenConnection();

        string externalOrderId = (string)row.external_order_id;
        int pickupStoreId = (int)row.pickup_store_id;
        string statusCode = (string)(row.StatusCode ?? "new");
        string sourceCode = (string)(row.SourceCode ?? "pixfizz");
        int orderStatusId = SyncMapper.StatusCodeToStatusId(statusCode);
        int orderSourceId = SyncMapper.SourceCodeToSourceId(sourceCode);

        if (existingLocalId != null)
        {
            // Compare updated_at — only update if MariaDB is newer
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT updated_at FROM orders WHERE id = @id";
            checkCmd.Parameters.AddWithValue("@id", existingLocalId);
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
                    is_held = @held,
                    updated_at = @updatedAt
                WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("@held", Convert.ToBoolean(row.is_held) ? 1 : 0);
            cmd.Parameters.AddWithValue("@updatedAt", ((DateTime)row.updated_at).ToString("o"));
            cmd.Parameters.AddWithValue("@id", existingLocalId);
            cmd.ExecuteNonQuery();
        }
        else
        {
            // Insert new
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO orders (
                    id, external_order_id, pickup_store_id, order_source_id, source_code,
                    order_status_id, status_code,
                    customer_first_name, customer_last_name, customer_email, customer_phone,
                    total_amount, is_held, is_transfer, transfer_store_id,
                    special_instructions, folder_path, delivery_method_id, ordered_at,
                    harvested_by_store_id, is_printed, display_tab,
                    created_at, updated_at
                ) VALUES (
                    @id, @eid, @store, @srcId, @srcCode,
                    @statusId, @statusCode,
                    @fname, @lname, @email, @phone,
                    @total, @held, @transfer, @transferStore,
                    @instructions, @folder, @delivery, @orderedAt,
                    @harvestedBy, @isPrinted, @displayTab,
                    @createdAt, @updatedAt
                )
                """;
            cmd.Parameters.AddWithValue("@id", Convert.ToString(row.id)!);
            cmd.Parameters.AddWithValue("@eid", externalOrderId);
            cmd.Parameters.AddWithValue("@store", pickupStoreId);
            BindPullInsertParams(cmd, row, sourceCode, orderSourceId, statusCode, orderStatusId);
            cmd.Parameters.AddWithValue("@createdAt", ((DateTime)row.created_at).ToString("o"));
            cmd.Parameters.AddWithValue("@updatedAt", ((DateTime)row.updated_at).ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Bind ALL fields for INSERT (new orders from another store). Standalone — not shared with UPDATE.</summary>
    private static void BindPullInsertParams(SqliteCommand cmd, dynamic row, string sourceCode, int orderSourceId, string statusCode, int orderStatusId)
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
        cmd.Parameters.AddWithValue("@delivery", row.delivery_method_id != null ? (int)row.delivery_method_id : 1);
        cmd.Parameters.AddWithValue("@orderedAt", row.ordered_at != null ? ((DateTime)row.ordered_at).ToString("o") : DateTime.Now.ToString("o"));
        cmd.Parameters.AddWithValue("@folder", (string?)row.folder_path ?? "");
        cmd.Parameters.AddWithValue("@harvestedBy", row.harvested_by_store_id != null ? (int)row.harvested_by_store_id : 0);
        cmd.Parameters.AddWithValue("@isPrinted", Convert.ToBoolean(row.is_printed) ? 1 : 0);
        cmd.Parameters.AddWithValue("@displayTab", row.display_tab != null ? (int)row.display_tab : (int)Core.Models.DisplayTab.Pending);
    }

    private void UpsertLocalItem(dynamic item)
    {
        string localOrderId = Convert.ToString(item.order_id)!;

        string sizeLabel = (string?)item.size_label ?? "";
        string mediaType = (string?)item.media_type ?? "";
        string imageFilename = (string?)item.image_filename ?? "";

        using var conn = _localDb.OpenConnection();

        // Verify the local order exists — skip orphan items
        using var existsCmd = conn.CreateCommand();
        existsCmd.CommandText = "SELECT COUNT(*) FROM orders WHERE id = @id";
        existsCmd.Parameters.AddWithValue("@id", localOrderId);
        if (Convert.ToInt32(existsCmd.ExecuteScalar()) == 0) return;

        // Check if item exists by natural key
        using var findCmd = conn.CreateCommand();
        findCmd.CommandText = """
            SELECT id FROM order_items
            WHERE order_id = @oid AND image_filename = @fname
              AND size_label = @size AND media_type = @media
            """;
        findCmd.Parameters.AddWithValue("@oid", localOrderId);
        findCmd.Parameters.AddWithValue("@fname", imageFilename);
        findCmd.Parameters.AddWithValue("@size", sizeLabel);
        findCmd.Parameters.AddWithValue("@media", mediaType);
        var existingId = findCmd.ExecuteScalar();

        if (existingId != null)
        {
            // Check item ownership — only accept is_printed from the producing store
            using var ownerCmd = conn.CreateCommand();
            ownerCmd.CommandText = "SELECT fulfillment_store_id FROM order_items WHERE id = @id";
            ownerCmd.Parameters.AddWithValue("@id", Convert.ToString(existingId));
            var localFulfillStore = ownerCmd.ExecuteScalar();
            int? fulfillStoreId = localFulfillStore is int fs ? fs : null;

            bool remotePrinted = Convert.ToBoolean(item.is_printed ?? false);
            int? remoteFulfillStore = item.fulfillment_store_id != null ? (int?)item.fulfillment_store_id : null;

            // Accept is_printed only if fulfillment_store_id matches (producing store owns the update)
            bool acceptPrinted = fulfillStoreId.HasValue && remoteFulfillStore.HasValue
                && fulfillStoreId.Value == remoteFulfillStore.Value;

            using var cmd = conn.CreateCommand();
            if (acceptPrinted)
            {
                cmd.CommandText = """
                    UPDATE order_items SET
                        quantity = @qty, image_filepath = @fpath,
                        options_json = @options,
                        is_printed = @printed, updated_at = datetime('now','localtime')
                    WHERE id = @id
                    """;
                cmd.Parameters.AddWithValue("@printed", remotePrinted ? 1 : 0);
            }
            else
            {
                cmd.CommandText = """
                    UPDATE order_items SET
                        quantity = @qty, image_filepath = @fpath,
                        options_json = @options,
                        updated_at = datetime('now','localtime')
                    WHERE id = @id
                    """;
            }
            cmd.Parameters.AddWithValue("@qty", (int)item.quantity);
            cmd.Parameters.AddWithValue("@fpath", (string?)item.image_filepath ?? "");
            cmd.Parameters.AddWithValue("@options", (string?)item.options_json ?? "[]");
            cmd.Parameters.AddWithValue("@id", Convert.ToString(existingId));
            cmd.ExecuteNonQuery();
        }
        else
        {
            // Insert new item
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO order_items (
                    id, order_id, size_label, media_type, quantity,
                    image_filename, image_filepath,
                    options_json, is_printed,
                    fulfillment_store_id, source_item_id, image_width, image_height
                ) VALUES (
                    @itemId, @oid, @size, @media, @qty,
                    @fname, @fpath,
                    @options, @printed,
                    @fulfillStore, @sourceItem, @imgW, @imgH
                )
                """;
            cmd.Parameters.AddWithValue("@itemId", Convert.ToString(item.id)!);
            cmd.Parameters.AddWithValue("@oid", localOrderId);
            cmd.Parameters.AddWithValue("@size", sizeLabel);
            cmd.Parameters.AddWithValue("@media", mediaType);
            cmd.Parameters.AddWithValue("@qty", (int)item.quantity);
            cmd.Parameters.AddWithValue("@fname", imageFilename);
            cmd.Parameters.AddWithValue("@fpath", (string?)item.image_filepath ?? "");
            cmd.Parameters.AddWithValue("@options", (string?)item.options_json ?? "[]");
            cmd.Parameters.AddWithValue("@printed", Convert.ToBoolean(item.is_printed ?? false) ? 1 : 0);
            cmd.Parameters.AddWithValue("@fulfillStore", item.fulfillment_store_id != null ? (object)(int)item.fulfillment_store_id : DBNull.Value);
            cmd.Parameters.AddWithValue("@sourceItem", item.source_item_id != null ? (object)Convert.ToString(item.source_item_id)! : DBNull.Value);
            cmd.Parameters.AddWithValue("@imgW", item.image_width != null ? (object)(int)item.image_width : DBNull.Value);
            cmd.Parameters.AddWithValue("@imgH", item.image_height != null ? (object)(int)item.image_height : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private async Task PurgeMariaDbNotesOnceAsync()
    {
        using var conn = _localDb.OpenConnection();
        using var check = conn.CreateCommand();
        check.CommandText = """
            CREATE TABLE IF NOT EXISTS migrations_applied (
                id TEXT PRIMARY KEY,
                applied_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            )
            """;
        check.ExecuteNonQuery();

        using var exists = conn.CreateCommand();
        exists.CommandText = "SELECT 1 FROM migrations_applied WHERE id = '013_purge_mariadb_notes'";
        if (exists.ExecuteScalar() != null) return;

        await _remoteDb.PurgeSystemNotesAsync();

        using var mark = conn.CreateCommand();
        mark.CommandText = "INSERT INTO migrations_applied (id) VALUES ('013_purge_mariadb_notes')";
        mark.ExecuteNonQuery();
    }

    private void InsertRemoteNote(dynamic note)
    {
        string mariaDbNoteId = Convert.ToString(note.id)!;
        string localOrderId = Convert.ToString(note.order_id)!;

        using var conn = _localDb.OpenConnection();

        // Verify the local order still exists
        using var existsCmd = conn.CreateCommand();
        existsCmd.CommandText = "SELECT COUNT(*) FROM orders WHERE id = @id";
        existsCmd.Parameters.AddWithValue("@id", localOrderId);
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

        // Skip system noise — ingest/verify bookkeeping doesn't belong in operator history
        if (noteText.StartsWith("Order received", StringComparison.OrdinalIgnoreCase) ||
            noteText.StartsWith("Verify:", StringComparison.OrdinalIgnoreCase) ||
            noteText.StartsWith("Repaired at", StringComparison.OrdinalIgnoreCase))
            return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO order_history (id, order_id, note, created_by, created_at, remote_id)
            VALUES (@id, @oid, @note, @by, @at, @rid)
            """;
        cmd.Parameters.AddWithValue("@id", mariaDbNoteId);
        cmd.Parameters.AddWithValue("@oid", localOrderId);
        cmd.Parameters.AddWithValue("@note", noteText);
        cmd.Parameters.AddWithValue("@by", employeeName);
        cmd.Parameters.AddWithValue("@at", createdAt);
        cmd.Parameters.AddWithValue("@rid", mariaDbNoteId);
        cmd.ExecuteNonQuery();
    }

    private void InsertRemoteLink(dynamic link)
    {
        // Explicit .ToString() — Dapper dynamic may return Guid objects for CHAR(36) columns
        // which SQLite rejects as "datatype mismatch" on TEXT columns with FK constraints.
        string linkId = link.id.ToString();
        string parentOrderId = link.parent_order_id.ToString();
        string childOrderId = link.child_order_id.ToString();
        string linkType = link.link_type.ToString();
        string createdBy = link.created_by?.ToString() ?? "";

        if (VerifyOrderExists(parentOrderId) == null || VerifyOrderExists(childOrderId) == null)
            return;

        using var conn = _localDb.OpenConnection();

        // Temporarily disable FK enforcement — the referenced orders may not
        // have been pulled yet this cycle. INSERT OR IGNORE handles duplicates;
        // orphaned links (parent/child missing) will be harmless until the
        // orders arrive on the next pull cycle.
        using var fkOff = conn.CreateCommand();
        fkOff.CommandText = "PRAGMA foreign_keys = OFF";
        fkOff.ExecuteNonQuery();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO order_links (id, parent_order_id, child_order_id, link_type, created_by)
            VALUES (@id, @pid, @cid, @type, @by)
            """;
        cmd.Parameters.AddWithValue("@id", linkId);
        cmd.Parameters.AddWithValue("@pid", parentOrderId);
        cmd.Parameters.AddWithValue("@cid", childOrderId);
        cmd.Parameters.AddWithValue("@type", linkType);
        cmd.Parameters.AddWithValue("@by", createdBy);
        cmd.ExecuteNonQuery();

        using var fkOn = conn.CreateCommand();
        fkOn.CommandText = "PRAGMA foreign_keys = ON";
        fkOn.ExecuteNonQuery();
    }

    // ── Outbox retry ─────────────────────────────────────────────────────

    public async Task ProcessOutboxAsync()
    {
        try
        {
            // Purge broken entries (empty record_id) that would retry forever
            _outbox.PurgeBrokenEntries();

            var pending = _outbox.GetPending();

            // Process insert_order first so the parent row exists in MariaDB
            // before items, links, or status updates reference it via FK.
            pending.Sort((a, b) =>
            {
                var aOrder = a.Operation == "insert_order" ? 0 : 1;
                var bOrder = b.Operation == "insert_order" ? 0 : 1;
                return aOrder != bOrder ? aOrder.CompareTo(bOrder) : a.Id.CompareTo(b.Id);
            });

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

    private bool IsOurOrder(string localOrderId)
    {
        using var conn = _localDb.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pickup_store_id FROM orders WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", localOrderId);
        var result = cmd.ExecuteScalar();
        if (result == null) return false;
        return Convert.ToInt32(result) == _settings.StoreId;
    }

    /// <summary>Verify the order exists locally. Returns the order ID if found, null otherwise.</summary>
    private string? VerifyOrderExists(string orderId)
    {
        using var conn = _localDb.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM orders WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", orderId);
        var exists = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        return exists ? orderId : null;
    }

    /// <summary>
    /// Ensure the order exists in MariaDB and return its MariaDB ID.
    /// The MariaDB ID may differ from the local GUID when the other store
    /// pushed the order first. Returns null on failure.
    /// </summary>
    private async Task<string?> EnsureOrderInMariaDbAsync(string localOrderId)
    {
        if (await _remoteDb.OrderExistsAsync(localOrderId))
            return localOrderId;

        AppLog.Info($"SyncPush: order {localOrderId} missing in MariaDB, pushing now");
        var payload = new Dictionary<string, JsonElement>
        {
            ["localOrderId"] = JsonSerializer.SerializeToElement(localOrderId)
        };
        return await PushInsertOrderAsync(payload);
    }

    private void AddOrdersMissingItems(List<string> mariaDbOrderIds)
    {
        using var conn = _localDb.OpenConnection();
        using var cmd = conn.CreateCommand();
        // Find orders with 0 local items (missed on prior pull).
        cmd.CommandText = """
            SELECT o.id FROM orders o
            WHERE NOT EXISTS (
                SELECT 1 FROM order_items i WHERE i.order_id = o.id
            )
            LIMIT 100
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var orderId = reader.GetString(0);
            if (!mariaDbOrderIds.Contains(orderId))
                mariaDbOrderIds.Add(orderId);
        }
    }

    private static string GetOrderIdFromPayload(string payloadJson)
    {
        try
        {
            var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("orderId", out var oid))
                return oid.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("localOrderId", out var lid))
                return lid.GetString() ?? "";
        }
        catch { }
        return "";
    }
}
