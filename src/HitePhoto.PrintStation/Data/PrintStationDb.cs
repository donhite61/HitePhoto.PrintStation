using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using HitePhoto.Shared.Models;
using HitePhoto.PrintStation.Core;

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

    /// <summary>Set the channel number on an order item.</summary>
    public async Task<bool> UpdateItemChannelAsync(int itemId, int channelNumber)
    {
        const string sql = "UPDATE order_items SET channel_number = @Channel WHERE id = @ItemId";

        try
        {
            await using var conn = CreateConnection();
            await conn.ExecuteAsync(sql, new { ItemId = itemId, Channel = channelNumber });
            return true;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to update item channel",
                detail: $"Attempted: UPDATE item {itemId} channel to {channelNumber}. Found: exception.",
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
}
