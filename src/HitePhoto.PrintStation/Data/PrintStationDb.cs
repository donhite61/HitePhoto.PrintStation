using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using HitePhoto.Shared.Models;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Data;

/// <summary>
/// Dapper repository for all PrintStation DB queries against MariaDB.
/// Uses HitePhoto.Shared models directly — no local order models needed.
/// </summary>
public class PrintStationDb
{
    private readonly string _connectionString;

    public PrintStationDb(string connectionString)
    {
        _connectionString = connectionString;
        // Dapper: map snake_case DB columns to PascalCase C# properties
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    private MySqlConnection CreateConnection() => new(_connectionString);

    /// <summary>Test the database connection. Returns null on success, error message on failure.</summary>
    public async Task<string?> TestConnectionAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync();
            await conn.ExecuteScalarAsync("SELECT 1");
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ── Order queries ────────────────────────────────────────────────────

    /// <summary>
    /// Get pending orders (new, in_progress) for a store.
    /// Includes joined store name, status code, and source code.
    /// </summary>
    public async Task<List<Order>> GetPendingOrdersAsync(int storeId)
    {
        const string sql = """
            SELECT o.*,
                   s.store_name  AS StoreName,
                   os.status_code AS StatusCode,
                   src.source_code AS SourceCode
            FROM orders o
            JOIN stores s ON s.id = o.pickup_store_id
            JOIN order_statuses os ON os.id = o.order_status_id
            JOIN order_sources src ON src.id = o.order_source_id
            WHERE o.pickup_store_id = @StoreId
              AND o.order_status_id IN (1, 2)
              AND o.is_test = 0
            ORDER BY o.ordered_at DESC, o.created_at DESC
            """;

        try
        {
            await using var conn = CreateConnection();
            var orders = (await conn.QueryAsync<Order>(sql, new { StoreId = storeId })).ToList();
            return orders;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to load pending orders",
                detail: $"Attempted: SELECT pending orders for store {storeId}. " +
                        $"Expected: list of orders with status new(1) or in_progress(2). " +
                        $"Found: exception. Connection: {_connectionString}",
                ex: ex);
            return new List<Order>();
        }
    }

    /// <summary>
    /// Get printed/completed orders (ready, notified, picked_up) for a store.
    /// </summary>
    public async Task<List<Order>> GetPrintedOrdersAsync(int storeId, int daysBack = 14)
    {
        const string sql = """
            SELECT o.*,
                   s.store_name  AS StoreName,
                   os.status_code AS StatusCode,
                   src.source_code AS SourceCode
            FROM orders o
            JOIN stores s ON s.id = o.pickup_store_id
            JOIN order_statuses os ON os.id = o.order_status_id
            JOIN order_sources src ON src.id = o.order_source_id
            WHERE o.pickup_store_id = @StoreId
              AND o.order_status_id IN (4, 5, 6)
              AND o.is_test = 0
              AND o.updated_at >= @Since
            ORDER BY o.updated_at DESC
            """;

        try
        {
            await using var conn = CreateConnection();
            var orders = (await conn.QueryAsync<Order>(sql, new
            {
                StoreId = storeId,
                Since = DateTime.Now.AddDays(-daysBack)
            })).ToList();
            return orders;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to load printed orders",
                detail: $"Attempted: SELECT printed orders for store {storeId}, last {daysBack} days. " +
                        $"Expected: list of orders with status ready(4)/notified(5)/picked_up(6). " +
                        $"Found: exception. Connection: {_connectionString}",
                ex: ex);
            return new List<Order>();
        }
    }

    /// <summary>
    /// Get orders belonging to other stores (for the Other Store tab).
    /// Excludes terminal statuses (picked_up, cancelled).
    /// </summary>
    public async Task<List<Order>> GetOtherStoreOrdersAsync(int storeId)
    {
        const string sql = """
            SELECT o.*,
                   s.store_name  AS StoreName,
                   os.status_code AS StatusCode,
                   src.source_code AS SourceCode
            FROM orders o
            JOIN stores s ON s.id = o.pickup_store_id
            JOIN order_statuses os ON os.id = o.order_status_id
            JOIN order_sources src ON src.id = o.order_source_id
            WHERE o.pickup_store_id != @StoreId
              AND o.order_status_id NOT IN (6, 7)
              AND o.is_test = 0
            ORDER BY o.ordered_at DESC, o.created_at DESC
            """;

        try
        {
            await using var conn = CreateConnection();
            var orders = (await conn.QueryAsync<Order>(sql, new { StoreId = storeId })).ToList();
            return orders;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to load other store orders",
                detail: $"Attempted: SELECT orders where pickup_store_id != {storeId}. " +
                        $"Expected: list of non-terminal orders for other stores. " +
                        $"Found: exception. Connection: {_connectionString}",
                ex: ex);
            return new List<Order>();
        }
    }

    // ── Order items ──────────────────────────────────────────────────────

    /// <summary>Get all items for an order.</summary>
    public async Task<List<OrderItem>> GetOrderItemsAsync(int orderId)
    {
        const string sql = "SELECT * FROM order_items WHERE order_id = @OrderId ORDER BY size_label, media_type";

        try
        {
            await using var conn = CreateConnection();
            return (await conn.QueryAsync<OrderItem>(sql, new { OrderId = orderId })).ToList();
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to load order items",
                detail: $"Attempted: SELECT items for order {orderId}. " +
                        $"Expected: list of order_items rows. Found: exception.",
                ex: ex);
            return new List<OrderItem>();
        }
    }

    /// <summary>
    /// Get items for multiple orders in a single query (batch load for pending tab).
    /// Returns a dictionary keyed by order ID.
    /// </summary>
    public async Task<Dictionary<int, List<OrderItem>>> GetOrderItemsBatchAsync(IEnumerable<int> orderIds)
    {
        var idList = orderIds.ToList();
        if (idList.Count == 0)
            return new Dictionary<int, List<OrderItem>>();

        const string sql = "SELECT * FROM order_items WHERE order_id IN @OrderIds ORDER BY order_id, size_label, media_type";

        try
        {
            await using var conn = CreateConnection();
            var items = (await conn.QueryAsync<OrderItem>(sql, new { OrderIds = idList })).ToList();
            return items.GroupBy(i => i.OrderId).ToDictionary(g => g.Key, g => g.ToList());
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to batch-load order items",
                detail: $"Attempted: SELECT items for {idList.Count} orders. " +
                        $"Expected: grouped item list. Found: exception.",
                ex: ex);
            return new Dictionary<int, List<OrderItem>>();
        }
    }

    // ── Order notes ──────────────────────────────────────────────────────

    /// <summary>Get notes for an order.</summary>
    public async Task<List<OrderNote>> GetOrderNotesAsync(int orderId)
    {
        const string sql = """
            SELECT n.*, CONCAT(e.first_name, ' ', e.last_name) AS EmployeeName
            FROM order_notes n
            LEFT JOIN employees e ON e.id = n.employee_id
            WHERE n.order_id = @OrderId
            ORDER BY n.created_at DESC
            """;

        try
        {
            await using var conn = CreateConnection();
            return (await conn.QueryAsync<OrderNote>(sql, new { OrderId = orderId })).ToList();
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to load order notes",
                detail: $"Attempted: SELECT notes for order {orderId}. Found: exception.",
                ex: ex);
            return new List<OrderNote>();
        }
    }

    // ── Stores lookup ────────────────────────────────────────────────────

    /// <summary>Get all stores for settings dropdown.</summary>
    public async Task<List<Store>> GetStoresAsync()
    {
        const string sql = "SELECT * FROM stores ORDER BY id";

        try
        {
            await using var conn = CreateConnection();
            return (await conn.QueryAsync<Store>(sql)).ToList();
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to load stores",
                detail: "Attempted: SELECT all stores. Found: exception.",
                ex: ex);
            return new List<Store>();
        }
    }

    // ── Status updates ───────────────────────────────────────────────────

    /// <summary>Update an order's status and write audit history.</summary>
    public async Task<bool> UpdateOrderStatusAsync(int orderId, int newStatusId, int? employeeId = null, string? notes = null)
    {
        const string sqlUpdate = """
            UPDATE orders SET order_status_id = @NewStatusId, updated_at = NOW()
            WHERE id = @OrderId
            """;

        const string sqlHistory = """
            INSERT INTO order_status_history (order_id, old_status_id, new_status_id, changed_by_employee_id, notes)
            SELECT @OrderId, order_status_id, @NewStatusId, @EmployeeId, @Notes
            FROM orders WHERE id = @OrderId
            """;

        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await conn.ExecuteAsync(sqlHistory, new { OrderId = orderId, NewStatusId = newStatusId, EmployeeId = employeeId, Notes = notes }, tx);
            await conn.ExecuteAsync(sqlUpdate, new { OrderId = orderId, NewStatusId = newStatusId }, tx);

            await tx.CommitAsync();
            return true;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to update order status",
                detail: $"Attempted: UPDATE order {orderId} to status {newStatusId}. " +
                        $"Expected: status updated + history written. Found: exception.",
                ex: ex);
            return false;
        }
    }

    /// <summary>Toggle hold on an order and write a note.</summary>
    public async Task<bool> ToggleHoldAsync(int orderId, bool isHeld, string? reason = null, int? employeeId = null)
    {
        const string sqlHold = "UPDATE orders SET is_held = @IsHeld, updated_at = NOW() WHERE id = @OrderId";

        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await conn.ExecuteAsync(sqlHold, new { OrderId = orderId, IsHeld = isHeld }, tx);

            if (!string.IsNullOrWhiteSpace(reason))
            {
                const string sqlNote = """
                    INSERT INTO order_notes (order_id, employee_id, note_text, note_type)
                    VALUES (@OrderId, @EmployeeId, @NoteText, 'hold')
                    """;
                await conn.ExecuteAsync(sqlNote, new { OrderId = orderId, EmployeeId = employeeId, NoteText = reason }, tx);
            }

            // If placing on hold, also set status to on_hold (3)
            // If releasing from hold, set back to in_progress (2)
            int newStatus = isHeld ? 3 : 2;
            const string sqlStatus = "UPDATE orders SET order_status_id = @NewStatus WHERE id = @OrderId";
            await conn.ExecuteAsync(sqlStatus, new { OrderId = orderId, NewStatus = newStatus }, tx);

            await tx.CommitAsync();
            return true;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                $"Failed to {(isHeld ? "hold" : "release")} order",
                detail: $"Attempted: toggle hold on order {orderId} to {isHeld}. Found: exception.",
                ex: ex);
            return false;
        }
    }

    /// <summary>Delete system-generated junk notes from MariaDB (one-time cleanup).</summary>
    public async Task PurgeSystemNotesAsync()
    {
        const string sql = """
            DELETE FROM order_notes
            WHERE note_text LIKE 'Order received%'
               OR note_text LIKE 'Verify:%'
               OR note_text LIKE 'Repaired at%'
            """;
        try
        {
            await using var conn = CreateConnection();
            var deleted = await conn.ExecuteAsync(sql);
            if (deleted > 0)
                AppLog.Info($"Purged {deleted} system notes from MariaDB");
        }
        catch (Exception ex)
        {
            AppLog.Info($"MariaDB note purge failed: {ex.Message}");
        }
    }

    /// <summary>Add a note to an order.</summary>
    public async Task<bool> AddNoteAsync(int orderId, int? employeeId, string noteText, string noteType = "general")
    {
        const string sql = """
            INSERT INTO order_notes (order_id, employee_id, note_text, note_type)
            VALUES (@OrderId, @EmployeeId, @NoteText, @NoteType)
            """;

        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(sql, new { OrderId = orderId, EmployeeId = employeeId, NoteText = noteText, NoteType = noteType });
            return true;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to add order note",
                detail: $"Attempted: INSERT note on order {orderId}. Found: exception.",
                ex: ex);
            return false;
        }
    }

    /// <summary>Mark a single item as printed.</summary>
    public async Task<bool> UpdateItemPrintedAsync(int itemId, DateTime printedAt)
    {
        const string sql = "UPDATE order_items SET is_printed = 1, printed_at = @PrintedAt WHERE id = @ItemId";

        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(sql, new { ItemId = itemId, PrintedAt = printedAt });
            return true;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to mark item as printed",
                detail: $"Attempted: UPDATE item {itemId} is_printed=1. Found: exception.",
                ex: ex);
            return false;
        }
    }

    /// <summary>Transfer an order to another store (DB metadata only — SFTP for files is separate).</summary>
    public async Task<bool> TransferOrderAsync(int orderId, int targetStoreId, string? note = null, int? employeeId = null)
    {
        const string sqlTransfer = """
            UPDATE orders SET
                current_location_store_id = @TargetStoreId,
                is_transfer = 1,
                transfer_store_id = @TargetStoreId,
                transfer_note = @Note,
                order_status_id = 8,
                updated_at = NOW()
            WHERE id = @OrderId
            """;

        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await conn.ExecuteAsync(sqlTransfer, new { OrderId = orderId, TargetStoreId = targetStoreId, Note = note }, tx);

            if (!string.IsNullOrWhiteSpace(note))
            {
                const string sqlNote = """
                    INSERT INTO order_notes (order_id, employee_id, note_text, note_type)
                    VALUES (@OrderId, @EmployeeId, @NoteText, 'transfer')
                    """;
                await conn.ExecuteAsync(sqlNote, new { OrderId = orderId, EmployeeId = employeeId, NoteText = note }, tx);
            }

            await tx.CommitAsync();
            return true;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to transfer order",
                detail: $"Attempted: transfer order {orderId} to store {targetStoreId}. Found: exception.",
                ex: ex);
            return false;
        }
    }

    // ── Alerts ───────────────────────────────────────────────────────────

    /// <summary>Ensure the alerts table exists in MariaDB. Called once at startup.</summary>
    public async Task EnsureAlertsTableAsync()
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS alerts (
                id            INT AUTO_INCREMENT PRIMARY KEY,
                store_id      INT NOT NULL,
                severity      VARCHAR(10) NOT NULL,
                category      VARCHAR(50) NOT NULL,
                summary       VARCHAR(500) NOT NULL,
                order_id      VARCHAR(100),
                detail        TEXT,
                exception     TEXT,
                source_method VARCHAR(200),
                source_file   VARCHAR(200),
                source_line   INT,
                created_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                acknowledged  TINYINT(1) NOT NULL DEFAULT 0,
                INDEX idx_alerts_store (store_id),
                INDEX idx_alerts_created (created_at),
                INDEX idx_alerts_unacked (acknowledged, store_id)
            )
            """;

        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(sql);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to create MariaDB alerts table: {ex.Message}");
        }
    }

    /// <summary>Insert an alert into MariaDB. Fire-and-forget — failures are logged only.</summary>
    public async Task InsertAlertAsync(int storeId, AlertRecord alert)
    {
        const string sql = """
            INSERT INTO alerts (store_id, severity, category, summary, order_id, detail, exception,
                                source_method, source_file, source_line, created_at)
            VALUES (@StoreId, @Severity, @Category, @Summary, @OrderId, @Detail, @Exception,
                    @Method, @File, @Line, @CreatedAt)
            """;

        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(sql, new
            {
                StoreId = storeId,
                alert.Severity,
                alert.Category,
                alert.Summary,
                alert.OrderId,
                alert.Detail,
                alert.Exception,
                Method = alert.SourceMethod,
                File = alert.SourceFile,
                Line = alert.SourceLine,
                CreatedAt = DateTime.TryParse(alert.CreatedAt, out var dt) ? dt.ToString("yyyy-MM-dd HH:mm:ss") : DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            });
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to push alert to MariaDB: {ex.Message}");
        }
    }

    /// <summary>Get recent alerts from MariaDB, optionally filtered by store.</summary>
    public async Task<List<(int StoreId, AlertRecord Alert)>> GetRecentAlertsAsync(int days, int? storeId = null)
    {
        var sql = """
            SELECT id, store_id, severity, category, summary, order_id, detail, exception,
                   source_method, source_file, source_line, created_at, acknowledged
            FROM alerts
            WHERE created_at >= @Since
            """ +
            (storeId.HasValue ? " AND store_id = @StoreId" : "") +
            " ORDER BY created_at DESC";

        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.QueryAsync(sql, new { Since = DateTime.Now.AddDays(-days), StoreId = storeId });
            return rows.Select(r => (
                StoreId: (int)r.store_id,
                Alert: new AlertRecord(
                    Id: (int)r.id,
                    Severity: (string)r.severity,
                    Category: (string)r.category,
                    Summary: (string)r.summary,
                    OrderId: (string?)r.order_id,
                    Detail: (string?)r.detail,
                    Exception: (string?)r.exception,
                    SourceMethod: (string?)r.source_method,
                    SourceFile: (string?)r.source_file,
                    SourceLine: r.source_line == null ? null : (int?)Convert.ToInt32(r.source_line),
                    CreatedAt: ((DateTime)r.created_at).ToString("yyyy-MM-dd HH:mm:ss"),
                    Acknowledged: Convert.ToBoolean(r.acknowledged))
            )).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to read alerts from MariaDB: {ex.Message}");
            return new List<(int, AlertRecord)>();
        }
    }

    // ── Sync: pull queries ────────────────────────────────────────────────

    /// <summary>Get all orders updated since a given timestamp (all stores).</summary>
    public async Task<List<dynamic>> GetOrdersUpdatedSinceAsync(DateTime since)
    {
        const string sql = """
            SELECT o.*,
                   os.status_code AS StatusCode,
                   src.source_code AS SourceCode
            FROM orders o
            JOIN order_statuses os ON os.id = o.order_status_id
            JOIN order_sources src ON src.id = o.order_source_id
            WHERE o.updated_at > @Since
            ORDER BY o.updated_at ASC
            """;

        try
        {
            await using var conn = CreateConnection();
            var rows = (await conn.QueryAsync(sql, new { Since = since })).ToList();
            return rows;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to pull orders from MariaDB",
                detail: $"Attempted: SELECT orders updated since {since:o}. " +
                        $"Expected: list of changed orders. " +
                        $"Found: {ex.GetType().Name}. " +
                        $"Context: sync pull. " +
                        $"State: local SQLite unaffected.",
                ex: ex);
            return new List<dynamic>();
        }
    }

    /// <summary>Get items for multiple orders in one query.</summary>
    public async Task<List<dynamic>> GetOrderItemsForOrdersAsync(List<int> mariaDbOrderIds)
    {
        if (mariaDbOrderIds.Count == 0)
            return new List<dynamic>();

        const string sql = "SELECT * FROM order_items WHERE order_id IN @OrderIds ORDER BY order_id, id";

        try
        {
            await using var conn = CreateConnection();
            return (await conn.QueryAsync(sql, new { OrderIds = mariaDbOrderIds })).ToList();
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to pull order items from MariaDB",
                detail: $"Attempted: SELECT items for {mariaDbOrderIds.Count} orders. " +
                        $"Expected: item list. " +
                        $"Found: {ex.GetType().Name}. " +
                        $"Context: sync pull. " +
                        $"State: local SQLite unaffected.",
                ex: ex);
            return new List<dynamic>();
        }
    }

    /// <summary>Get notes created since a given timestamp, with employee name.</summary>
    public async Task<List<dynamic>> GetOrderNotesSinceAsync(DateTime since)
    {
        const string sql = """
            SELECT n.id, n.order_id, n.note_text, n.note_type, n.created_at,
                   CONCAT(COALESCE(e.first_name, ''), ' ', COALESCE(e.last_name, '')) AS EmployeeName
            FROM order_notes n
            LEFT JOIN employees e ON e.id = n.employee_id
            WHERE n.created_at > @Since
            ORDER BY n.created_at ASC
            """;

        try
        {
            await using var conn = CreateConnection();
            return (await conn.QueryAsync(sql, new { Since = since })).ToList();
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to pull order notes from MariaDB",
                detail: $"Attempted: SELECT notes since {since:o}. " +
                        $"Expected: note list. " +
                        $"Found: {ex.GetType().Name}. " +
                        $"Context: sync pull. " +
                        $"State: local SQLite unaffected.",
                ex: ex);
            return new List<dynamic>();
        }
    }

    // ── Sync: push (upsert) ──────────────────────────────────────────────

    /// <summary>
    /// Upsert an order to MariaDB. Uses INSERT...ON DUPLICATE KEY UPDATE
    /// on (external_order_id, pickup_store_id). Returns the MariaDB order id.
    /// </summary>
    public async Task<int> UpsertOrderAsync(
        string externalOrderId, int pickupStoreId, int orderSourceId, int orderStatusId,
        string? customerFirstName, string? customerLastName, string? customerEmail, string? customerPhone,
        decimal? totalAmount, bool isHeld, bool isTransfer, int? transferStoreId,
        string? specialInstructions, string? folderPath, int deliveryMethodId,
        string? orderedAt, string? pixfizzJobId,
        bool isRush = false, string? paymentStatus = null,
        bool isNotified = false, string? notifiedAt = null,
        string? shippingFirstName = null, string? shippingLastName = null,
        string? shippingAddress1 = null, string? shippingAddress2 = null,
        string? shippingCity = null, string? shippingState = null,
        string? shippingZip = null, string? shippingCountry = null,
        string? shippingMethod = null,
        int harvestedByStoreId = 0, bool isPrinted = false,
        string? supersedes = null, string? alterationType = null)
    {
        const string sql = """
            INSERT INTO orders
                (external_order_id, pickup_store_id, current_location_store_id,
                 order_source_id, order_status_id,
                 customer_first_name, customer_last_name, customer_email, customer_phone,
                 total_amount, payment_status,
                 is_held, is_notified, notified_at,
                 is_transfer, transfer_store_id,
                 special_instructions, folder_path, delivery_method_id,
                 ordered_at, pixfizz_job_id, is_rush,
                 shipping_first_name, shipping_last_name,
                 shipping_address1, shipping_address2, shipping_city,
                 shipping_state, shipping_zip, shipping_country, shipping_method,
                 harvested_by_store_id, is_printed,
                 supersedes, alteration_type,
                 sync_status)
            VALUES
                (@Eid, @Store, @Store,
                 @SourceId, @StatusId,
                 @FirstName, @LastName, @Email, @Phone,
                 @Total, @PaymentStatus,
                 @Held, @Notified, @NotifiedAt,
                 @Transfer, @TransferStore,
                 @Instructions, @Folder, @Delivery,
                 @OrderedAt, @PixfizzJobId, @Rush,
                 @ShipFname, @ShipLname,
                 @ShipAddr1, @ShipAddr2, @ShipCity,
                 @ShipState, @ShipZip, @ShipCountry, @ShipMethod,
                 @HarvestedBy, @Printed,
                 @Supersedes, @AltType,
                 'synced')
            ON DUPLICATE KEY UPDATE
                order_status_id = VALUES(order_status_id),
                customer_first_name = COALESCE(VALUES(customer_first_name), customer_first_name),
                customer_last_name = COALESCE(VALUES(customer_last_name), customer_last_name),
                customer_email = COALESCE(VALUES(customer_email), customer_email),
                customer_phone = COALESCE(VALUES(customer_phone), customer_phone),
                total_amount = COALESCE(VALUES(total_amount), total_amount),
                payment_status = COALESCE(VALUES(payment_status), payment_status),
                is_held = VALUES(is_held),
                is_notified = VALUES(is_notified),
                notified_at = COALESCE(VALUES(notified_at), notified_at),
                is_transfer = VALUES(is_transfer),
                transfer_store_id = VALUES(transfer_store_id),
                special_instructions = COALESCE(VALUES(special_instructions), special_instructions),
                folder_path = COALESCE(VALUES(folder_path), folder_path),
                delivery_method_id = VALUES(delivery_method_id),
                ordered_at = COALESCE(VALUES(ordered_at), ordered_at),
                pixfizz_job_id = COALESCE(VALUES(pixfizz_job_id), pixfizz_job_id),
                is_rush = VALUES(is_rush),
                shipping_first_name = COALESCE(VALUES(shipping_first_name), shipping_first_name),
                shipping_last_name = COALESCE(VALUES(shipping_last_name), shipping_last_name),
                shipping_address1 = COALESCE(VALUES(shipping_address1), shipping_address1),
                shipping_address2 = COALESCE(VALUES(shipping_address2), shipping_address2),
                shipping_city = COALESCE(VALUES(shipping_city), shipping_city),
                shipping_state = COALESCE(VALUES(shipping_state), shipping_state),
                shipping_zip = COALESCE(VALUES(shipping_zip), shipping_zip),
                shipping_country = COALESCE(VALUES(shipping_country), shipping_country),
                shipping_method = COALESCE(VALUES(shipping_method), shipping_method),
                harvested_by_store_id = VALUES(harvested_by_store_id),
                is_printed = VALUES(is_printed),
                supersedes = COALESCE(VALUES(supersedes), supersedes),
                alteration_type = COALESCE(VALUES(alteration_type), alteration_type),
                sync_status = 'synced';
            SELECT LAST_INSERT_ID();
            """;

        try
        {
            await using var conn = CreateConnection();
            var id = await conn.ExecuteScalarAsync<int>(sql, new
            {
                Eid = externalOrderId,
                Store = pickupStoreId,
                SourceId = orderSourceId,
                StatusId = orderStatusId,
                FirstName = customerFirstName,
                LastName = customerLastName,
                Email = customerEmail,
                Phone = customerPhone,
                Total = totalAmount,
                PaymentStatus = paymentStatus,
                Held = isHeld ? 1 : 0,
                Notified = isNotified ? 1 : 0,
                NotifiedAt = DateTime.TryParse(notifiedAt, out var parsedNotified) ? parsedNotified.ToString("yyyy-MM-dd HH:mm:ss") : (object?)null,
                Transfer = isTransfer ? 1 : 0,
                TransferStore = transferStoreId,
                Instructions = specialInstructions,
                Folder = folderPath,
                Delivery = deliveryMethodId,
                OrderedAt = DateTime.TryParse(orderedAt, out var parsedDate) ? parsedDate.ToString("yyyy-MM-dd HH:mm:ss") : (object?)null,
                PixfizzJobId = pixfizzJobId,
                Rush = isRush ? 1 : 0,
                ShipFname = shippingFirstName,
                ShipLname = shippingLastName,
                ShipAddr1 = shippingAddress1,
                ShipAddr2 = shippingAddress2,
                ShipCity = shippingCity,
                ShipState = shippingState,
                ShipZip = shippingZip,
                ShipCountry = shippingCountry,
                ShipMethod = shippingMethod,
                HarvestedBy = harvestedByStoreId,
                Printed = isPrinted ? 1 : 0,
                Supersedes = supersedes,
                AltType = alterationType,
            });

            // LAST_INSERT_ID returns 0 on update — need to query by natural key
            if (id == 0)
            {
                id = await conn.ExecuteScalarAsync<int>(
                    "SELECT id FROM orders WHERE external_order_id = @Eid AND pickup_store_id = @Store",
                    new { Eid = externalOrderId, Store = pickupStoreId });
            }
            return id;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to upsert order to MariaDB",
                detail: $"Attempted: upsert order '{externalOrderId}' store {pickupStoreId}. " +
                        $"Expected: order inserted/updated. " +
                        $"Found: {ex.GetType().Name}. " +
                        $"Context: sync push. " +
                        $"State: queued in outbox for retry.",
                ex: ex);
            return 0;
        }
    }

    /// <summary>Upsert order items to MariaDB. Deletes existing items and re-inserts. Retries on deadlock.</summary>
    public async Task<bool> UpsertOrderItemsAsync(int mariaDbOrderId, List<(string SizeLabel, string MediaType, int Quantity, string ImageFilename, string ImageFilepath, string OriginalImageFilepath, string OptionsJson, bool IsPrinted)> items)
    {
        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await using var conn = CreateConnection();
                await conn.OpenAsync();
                await using var tx = await conn.BeginTransactionAsync();

                await conn.ExecuteAsync(
                    "DELETE FROM order_items WHERE order_id = @OrderId",
                    new { OrderId = mariaDbOrderId }, tx);

                foreach (var item in items)
                {
                    await conn.ExecuteAsync("""
                        INSERT INTO order_items
                            (order_id, size_label, media_type, quantity,
                             image_filename, image_filepath, original_image_filepath,
                             options_json, is_printed)
                        VALUES
                            (@OrderId, @Size, @Media, @Qty,
                             @Filename, @Filepath, @OrigFilepath,
                             @Options, @Printed)
                        """,
                        new
                        {
                            OrderId = mariaDbOrderId,
                            Size = item.SizeLabel,
                            Media = item.MediaType,
                            Qty = item.Quantity,
                            Filename = item.ImageFilename,
                            Filepath = item.ImageFilepath,
                            OrigFilepath = item.OriginalImageFilepath,
                            Options = item.OptionsJson,
                            Printed = item.IsPrinted ? 1 : 0,
                        }, tx);
                }

                await tx.CommitAsync();
                return true;
            }
            catch (MySqlConnector.MySqlException ex) when (ex.Number == 1213 && attempt < maxRetries)
            {
                // Deadlock — wait briefly and retry
                AppLog.Info($"UpsertOrderItems: deadlock on order {mariaDbOrderId}, retry {attempt}/{maxRetries}");
                await Task.Delay(100 * attempt);
            }
            catch (Exception ex)
            {
                AlertCollector.Error(AlertCategory.Database,
                    "Failed to upsert order items to MariaDB",
                    detail: $"Attempted: upsert {items.Count} items for MariaDB order {mariaDbOrderId}. " +
                            $"Expected: items replaced. " +
                            $"Found: {ex.GetType().Name}. " +
                            $"Context: sync push (attempt {attempt}/{maxRetries}). " +
                            $"State: queued in outbox for retry.",
                    ex: ex);
                return false;
            }
        }
        return false;
    }

    public async Task<bool> SetOrderPrintedAsync(int mariaDbOrderId, bool printed)
    {
        const string sql = "UPDATE orders SET is_printed = @Val WHERE id = @Id";
        try
        {
            await using var conn = CreateConnection();
            var rows = await conn.ExecuteAsync(sql, new { Val = printed ? 1 : 0, Id = mariaDbOrderId });
            return rows > 0;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                $"Failed to set is_printed on MariaDB order {mariaDbOrderId}",
                detail: $"Attempted: UPDATE orders SET is_printed={printed} WHERE id={mariaDbOrderId}. " +
                        $"Expected: 1 row updated. " +
                        $"Found: {ex.GetType().Name}: {ex.Message}. " +
                        $"Context: sync push create_alteration. " +
                        $"State: parent order is_printed not updated in MariaDB.",
                ex: ex);
            return false;
        }
    }

    public async Task<bool> InsertOrderLinkAsync(int parentId, int childId, string linkType, string createdBy)
    {
        const string sql = """
            INSERT INTO order_links (parent_order_id, child_order_id, link_type, created_by)
            VALUES (@Parent, @Child, @Type, @By)
            """;
        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(sql, new { Parent = parentId, Child = childId, Type = linkType, By = createdBy });
            return true;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                $"Failed to insert order_link in MariaDB ({linkType}: {parentId} → {childId})",
                detail: $"Attempted: INSERT order_links parent={parentId} child={childId} type={linkType}. " +
                        $"Expected: row inserted. " +
                        $"Found: {ex.GetType().Name}: {ex.Message}. " +
                        $"Context: sync push create_alteration. " +
                        $"State: order_links row missing in MariaDB.",
                ex: ex);
            return false;
        }
    }

    /// <summary>Find MariaDB order ID by natural key.</summary>
    public async Task<int?> FindOrderIdByNaturalKeyAsync(string externalOrderId, int pickupStoreId)
    {
        const string sql = "SELECT id FROM orders WHERE external_order_id = @Eid AND pickup_store_id = @Store";

        try
        {
            await using var conn = CreateConnection();
            return await conn.ExecuteScalarAsync<int?>(sql, new { Eid = externalOrderId, Store = pickupStoreId });
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to lookup MariaDB order ID",
                detail: $"Attempted: find order '{externalOrderId}' store {pickupStoreId}. " +
                        $"Expected: MariaDB order id. " +
                        $"Found: {ex.GetType().Name}. " +
                        $"Context: id_map population. " +
                        $"State: push will be queued in outbox.",
                ex: ex);
            return null;
        }
    }
}
