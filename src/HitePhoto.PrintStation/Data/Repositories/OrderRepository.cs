using System.IO;
using Microsoft.Data.Sqlite;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Core.Ingest;
using HitePhoto.PrintStation.Core.Models;

namespace HitePhoto.PrintStation.Data.Repositories;

public class OrderRepository : IOrderRepository
{
    private readonly OrderDb _db;
    private const string OrderSelectBase = """
        SELECT o.id, o.external_order_id, o.source_code, o.status_code,
               o.customer_first_name, o.customer_last_name,
               o.customer_email, o.customer_phone,
               o.ordered_at, o.total_amount, o.is_held, o.is_transfer,
               o.folder_path, o.special_instructions, o.download_status,
               s.short_name AS store_name,
               o.printed_at, o.created_at
        FROM orders o
        LEFT JOIN stores s ON s.id = o.pickup_store_id

        """;

    public OrderRepository(OrderDb db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public OrderRecord? GetOrder(string orderId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, external_order_id, source_code, pickup_store_id,
                   customer_email, folder_path, is_held, is_externally_modified
            FROM orders WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", orderId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new OrderRecord(
            Id: reader.GetString(0),
            ExternalOrderId: reader.GetString(1),
            Source: OrderSourceExtensions.FromCode(reader.GetString(2)),
            PickupStoreId: reader.GetInt32(3),
            CustomerEmail: reader.IsDBNull(4) ? "" : reader.GetString(4),
            FolderPath: reader.IsDBNull(5) ? "" : reader.GetString(5),
            IsHeld: reader.GetInt32(6) == 1,
            IsExternallyModified: reader.GetInt32(7) == 1);
    }

    public HitePhoto.Shared.Models.Order? GetFullOrder(string orderId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT o.id, o.external_order_id, o.ordered_at,
                   o.customer_first_name, o.customer_last_name,
                   o.customer_email, o.customer_phone,
                   o.pickup_store_id, o.folder_path,
                   s.store_name, o.delivery_method_id
            FROM orders o
            LEFT JOIN stores s ON s.id = o.pickup_store_id
            WHERE o.id = @id
            """;
        cmd.Parameters.AddWithValue("@id", orderId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new HitePhoto.Shared.Models.Order
        {
            Id = reader.GetString(0),
            ExternalOrderId = reader.GetString(1),
            OrderedAt = reader.IsDBNull(2) ? null : DateTime.Parse(reader.GetString(2)),
            CustomerFirstName = reader.IsDBNull(3) ? null : reader.GetString(3),
            CustomerLastName = reader.IsDBNull(4) ? null : reader.GetString(4),
            CustomerEmail = reader.IsDBNull(5) ? null : reader.GetString(5),
            CustomerPhone = reader.IsDBNull(6) ? null : reader.GetString(6),
            PickupStoreId = reader.GetInt32(7),
            FolderPath = reader.IsDBNull(8) ? null : reader.GetString(8),
            StoreName = reader.IsDBNull(9) ? null : reader.GetString(9),
            DeliveryMethodId = reader.IsDBNull(10) ? Core.DeliveryMethodId.Pickup : reader.GetInt32(10)
        };
    }

    public List<OrderItemRecord> GetNoritsuItems(string orderId)
    {
        var items = new List<OrderItemRecord>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, order_id, size_label, media_type, image_filepath,
                   quantity, is_noritsu, is_local_production, is_printed,
                   image_filename, options_json
            FROM order_items
            WHERE order_id = @id AND is_noritsu = 1
            ORDER BY size_label, media_type
            """;
        cmd.Parameters.AddWithValue("@id", orderId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new OrderItemRecord(
                Id: reader.GetString(0),
                OrderId: reader.GetString(1),
                SizeLabel: reader.GetString(2),
                MediaType: reader.IsDBNull(3) ? "" : reader.GetString(3),
                ImageFilepath: reader.IsDBNull(4) ? "" : reader.GetString(4),
                Quantity: reader.GetInt32(5),
                IsNoritsu: reader.GetInt32(6) == 1,
                IsLocalProduction: reader.GetInt32(7) == 1,
                IsPrinted: reader.GetInt32(8) == 1,
                ImageFilename: reader.IsDBNull(9) ? "" : reader.GetString(9),
                OptionsJson: reader.IsDBNull(10) ? "[]" : reader.GetString(10)));
        }
        return items;
    }

    public void SetHold(string orderId, bool isHeld)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE orders SET is_held = @held, updated_at = datetime('now','localtime') WHERE id = @id";
        cmd.Parameters.AddWithValue("@held", isHeld ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", orderId);
        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
            AlertCollector.Error(AlertCategory.Database,
                $"SetHold: order {orderId} not found",
                orderId: orderId.ToString(),
                detail: $"Attempted: UPDATE orders SET is_held={isHeld} WHERE id={orderId}. " +
                        $"Expected: 1 row updated. Found: 0 rows. " +
                        $"Context: isHeld={isHeld}. State: no matching order in SQLite");
    }

    public void SetNotified(string orderId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE orders SET is_notified = 1, updated_at = datetime('now','localtime') WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", orderId);
        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
            AlertCollector.Error(AlertCategory.Database,
                $"SetNotified: order {orderId} not found",
                orderId: orderId.ToString(),
                detail: $"Attempted: UPDATE orders SET is_notified=1 WHERE id={orderId}. " +
                        $"Expected: 1 row updated. Found: 0 rows. " +
                        $"Context: marking order notified. State: no matching order in SQLite");
    }

    public void SetCurrentLocation(string orderId, int storeId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE orders SET current_location_store_id = @store, updated_at = datetime('now','localtime')
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@store", storeId);
        cmd.Parameters.AddWithValue("@id", orderId);
        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
            AlertCollector.Error(AlertCategory.Database,
                $"SetCurrentLocation: order {orderId} not found",
                orderId: orderId.ToString(),
                detail: $"Attempted: UPDATE orders SET current_location_store_id={storeId} WHERE id={orderId}. " +
                        $"Expected: 1 row updated. Found: 0 rows. " +
                        $"Context: storeId={storeId}. State: no matching order in SQLite");
    }

    public void SetItemsPrinted(List<string> itemIds)
    {
        if (itemIds.Count == 0) return;
        using var conn = _db.OpenConnection();
        foreach (var id in itemIds)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE order_items SET is_printed = 1, updated_at = datetime('now','localtime') WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            var rows = cmd.ExecuteNonQuery();
            if (rows == 0)
                AlertCollector.Error(AlertCategory.Database,
                    $"SetItemsPrinted: item {id} not found",
                    detail: $"Attempted: UPDATE order_items SET is_printed=1 WHERE id={id}. " +
                            $"Expected: 1 row updated. Found: 0 rows. " +
                            $"Context: marking item printed. State: no matching item in SQLite");
        }
    }

    public void SetItemsUnprinted(string orderId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE order_items SET is_printed = 0, updated_at = datetime('now','localtime') WHERE order_id = @oid";
        cmd.Parameters.AddWithValue("@oid", orderId);
        cmd.ExecuteNonQuery();
    }

    public string? GetOrderIdForItem(string itemId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT order_id FROM order_items WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", itemId);
        return cmd.ExecuteScalar() as string;
    }

    public string GetStoreName(int storeId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT short_name FROM stores WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", storeId);
        return (string?)cmd.ExecuteScalar() ?? $"store {storeId}";
    }

    public string? FindOrderId(string externalOrderId, int storeId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM orders WHERE external_order_id = @eid AND pickup_store_id = @store";
        cmd.Parameters.AddWithValue("@eid", externalOrderId);
        cmd.Parameters.AddWithValue("@store", storeId);
        var result = cmd.ExecuteScalar();
        return result?.ToString();
    }

    public string? FindOrderIdAnyStore(string externalOrderId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM orders WHERE external_order_id = @eid LIMIT 1";
        cmd.Parameters.AddWithValue("@eid", externalOrderId);
        var result = cmd.ExecuteScalar();
        return result?.ToString();
    }

    public string? FindOrderIdByPattern(string pattern)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM orders WHERE external_order_id LIKE @pattern LIMIT 1";
        cmd.Parameters.AddWithValue("@pattern", pattern);
        var result = cmd.ExecuteScalar();
        return result?.ToString();
    }

    public List<OrderItemRecord> GetItems(string orderId)
    {
        var items = new List<OrderItemRecord>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, order_id, size_label, media_type, image_filepath,
                   quantity, is_noritsu, is_local_production, is_printed,
                   image_filename, category, sub_category
            FROM order_items
            WHERE order_id = @id
            ORDER BY size_label, media_type
            """;
        cmd.Parameters.AddWithValue("@id", orderId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new OrderItemRecord(
                Id: reader.GetString(0),
                OrderId: reader.GetString(1),
                SizeLabel: reader.IsDBNull(2) ? "" : reader.GetString(2),
                MediaType: reader.IsDBNull(3) ? "" : reader.GetString(3),
                ImageFilepath: reader.IsDBNull(4) ? "" : reader.GetString(4),
                Quantity: reader.GetInt32(5),
                IsNoritsu: reader.GetInt32(6) == 1,
                IsLocalProduction: reader.GetInt32(7) == 1,
                IsPrinted: reader.GetInt32(8) == 1,
                ImageFilename: reader.IsDBNull(9) ? "" : reader.GetString(9),
                Category: reader.IsDBNull(10) ? "" : reader.GetString(10),
                SubCategory: reader.IsDBNull(11) ? "" : reader.GetString(11)));
        }
        return items;
    }

    public void UpdateItem(string itemId, string sizeLabel, string mediaType,
        string imageFilename, string imageFilepath, int quantity,
        bool isNoritsu, string category, string subCategory)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE order_items SET
                size_label = @size, media_type = @media,
                image_filename = @fname, image_filepath = @fpath,
                quantity = @qty, is_noritsu = @noritsu,
                category = @cat, sub_category = @subcat,
                updated_at = datetime('now','localtime')
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@size", sizeLabel);
        cmd.Parameters.AddWithValue("@media", mediaType);
        cmd.Parameters.AddWithValue("@fname", imageFilename);
        cmd.Parameters.AddWithValue("@fpath", imageFilepath);
        cmd.Parameters.AddWithValue("@qty", quantity);
        cmd.Parameters.AddWithValue("@noritsu", isNoritsu ? 1 : 0);
        cmd.Parameters.AddWithValue("@cat", category);
        cmd.Parameters.AddWithValue("@subcat", subCategory);
        cmd.Parameters.AddWithValue("@id", itemId);
        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
            AlertCollector.Error(AlertCategory.Database,
                $"UpdateItem: item {itemId} not found",
                detail: $"Attempted: UPDATE order_items WHERE id={itemId}. " +
                        $"Expected: 1 row updated. Found: 0 rows. " +
                        $"Context: size={sizeLabel}, file={imageFilename}. State: no matching item in SQLite");
    }

    public void InsertItemOptions(string orderItemId, List<HitePhoto.Shared.Parsers.OrderItemOption> options)
    {
        if (options.Count == 0) return;
        using var conn = _db.OpenConnection();
        foreach (var opt in options)
        {
            var optId = Guid.NewGuid().ToString();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO order_item_options (id, order_item_id, option_key, option_value)
                VALUES (@optId, @itemId, @key, @value)
                """;
            cmd.Parameters.AddWithValue("@optId", optId);
            cmd.Parameters.AddWithValue("@itemId", orderItemId);
            cmd.Parameters.AddWithValue("@key", opt.Key);
            cmd.Parameters.AddWithValue("@value", opt.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public List<HitePhoto.Shared.Parsers.OrderItemOption> GetItemOptions(string orderItemId)
    {
        var options = new List<HitePhoto.Shared.Parsers.OrderItemOption>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT option_key, option_value FROM order_item_options WHERE order_item_id = @id";
        cmd.Parameters.AddWithValue("@id", orderItemId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            options.Add(new HitePhoto.Shared.Parsers.OrderItemOption(reader.GetString(0), reader.GetString(1)));
        return options;
    }

    public void DeleteItemOptions(string orderItemId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM order_item_options WHERE order_item_id = @id";
        cmd.Parameters.AddWithValue("@id", orderItemId);
        cmd.ExecuteNonQuery();
    }

    public void InsertItem(string orderId, UnifiedOrderItem item)
    {
        using var conn = _db.OpenConnection();
        InsertItemCore(conn, null, orderId, item, isPrinted: false);
    }

    private static void InsertItemCore(SqliteConnection conn, SqliteTransaction? transaction,
        string orderId, UnifiedOrderItem item, bool isPrinted,
        int? fulfillmentStoreId = null, int? localStoreId = null, string? sourceItemId = null)
    {
        var itemId = Guid.NewGuid().ToString();
        using var cmd = conn.CreateCommand();
        if (transaction != null) cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO order_items (
                id, order_id, size_label, media_type, category, sub_category,
                quantity, image_filename, image_filepath, original_image_filepath,
                is_noritsu, is_local_production, is_printed, options_json,
                fulfillment_store_id, source_item_id, image_width, image_height
            ) VALUES (
                @itemId, @oid, @size, @media, @cat, @subcat,
                @qty, @fname, @fpath, @orig,
                @noritsu, @localProd, @printed, @options,
                @fulfillStore, @sourceItem, @imgW, @imgH
            )
            """;
        cmd.Parameters.AddWithValue("@itemId", itemId);
        cmd.Parameters.AddWithValue("@oid", orderId);
        cmd.Parameters.AddWithValue("@size", item.SizeLabel ?? "");
        cmd.Parameters.AddWithValue("@media", item.MediaType ?? "");
        cmd.Parameters.AddWithValue("@cat", item.Options.FirstOrDefault(o => o.Key == "Category")?.Value ?? "");
        cmd.Parameters.AddWithValue("@subcat", item.Options.FirstOrDefault(o => o.Key == "SubCategory")?.Value ?? "");
        cmd.Parameters.AddWithValue("@qty", item.Quantity);
        cmd.Parameters.AddWithValue("@fname", item.ImageFilename ?? "");
        cmd.Parameters.AddWithValue("@fpath", item.ImageFilepath ?? "");
        cmd.Parameters.AddWithValue("@orig", item.OriginalImageFilepath ?? item.ImageFilepath ?? "");
        cmd.Parameters.AddWithValue("@noritsu", item.IsNoritsu ? 1 : 0);
        cmd.Parameters.AddWithValue("@localProd", fulfillmentStoreId.HasValue && localStoreId.HasValue
            ? (fulfillmentStoreId.Value == localStoreId.Value ? 1 : 0)
            : (item.IsLocalProduction ? 1 : 0));
        cmd.Parameters.AddWithValue("@printed", isPrinted ? 1 : 0);
        cmd.Parameters.AddWithValue("@options", item.Options.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(item.Options)
            : "[]");
        cmd.Parameters.AddWithValue("@fulfillStore", (object?)fulfillmentStoreId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sourceItem", (object?)sourceItemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@imgW", item.ImageWidth > 0 ? (object)item.ImageWidth : DBNull.Value);
        cmd.Parameters.AddWithValue("@imgH", item.ImageHeight > 0 ? (object)item.ImageHeight : DBNull.Value);
        var rows = cmd.ExecuteNonQuery();
        if (rows != 1)
            AlertCollector.Error(AlertCategory.Database,
                $"InsertItemCore: expected 1 row inserted, got {rows}",
                orderId: orderId.ToString(),
                detail: $"Attempted: INSERT order_items for order {orderId}, file {item.ImageFilename}. " +
                        $"Expected: 1 row inserted. Found: {rows} rows. " +
                        $"Context: size={item.SizeLabel}, qty={item.Quantity}. " +
                        $"State: isPrinted={isPrinted}");
    }

    public void ReplaceItems(string orderId, List<UnifiedOrderItem> items)
    {
        using var conn = _db.OpenConnection();
        using var transaction = conn.BeginTransaction();

        // Snapshot printed state from existing items, keyed by size+stem
        var printedState = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT size_label, image_filename, is_printed FROM order_items WHERE order_id = @oid";
            cmd.Parameters.AddWithValue("@oid", orderId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = $"{reader.GetString(0)}|{Path.GetFileNameWithoutExtension(reader.GetString(1))}";
                if (!printedState.ContainsKey(key))
                    printedState[key] = reader.GetInt32(2) != 0;
            }
        }

        // Delete all existing items
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM order_items WHERE order_id = @oid";
            cmd.Parameters.AddWithValue("@oid", orderId);
            cmd.ExecuteNonQuery();
        }

        // Insert fresh from source, restoring printed state where matched
        foreach (var item in items)
        {
            var key = $"{item.SizeLabel}|{Path.GetFileNameWithoutExtension(item.ImageFilename)}";
            printedState.TryGetValue(key, out var wasPrinted);
            InsertItemCore(conn, transaction, orderId, item, isPrinted: wasPrinted);
        }

        transaction.Commit();
    }

    public Dictionary<string, (string Id, string FolderPath, string SourceCode)> GetRecentOrders(int days, int storeId)
    {
        var cutoff = days > 0 ? DateTime.Now.AddDays(-days) : DateTime.MinValue;
        var result = new Dictionary<string, (string Id, string FolderPath, string SourceCode)>(StringComparer.OrdinalIgnoreCase);
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT o.id, o.external_order_id, o.folder_path, o.source_code
            FROM orders o
            WHERE o.harvested_by_store_id = @storeId
              AND (@daysBack = 0 OR o.created_at >= @cutoff)
            """;
        cmd.Parameters.AddWithValue("@storeId", storeId);
        cmd.Parameters.AddWithValue("@daysBack", days);
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("yyyy-MM-dd"));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var eid = reader.GetString(1);
            result[eid] = (
                reader.GetString(0),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3));
        }
        return result;
    }

    public string InsertOrder(UnifiedOrder order, int storeId, int harvestedByStoreId = 0)
    {
        using var conn = _db.OpenConnection();
        using var transaction = conn.BeginTransaction();

        var orderId = Guid.NewGuid().ToString();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO orders (
                    id, external_order_id, order_source_id, source_code,
                    customer_first_name, customer_last_name, customer_email, customer_phone,
                    order_status_id, status_code, pickup_store_id,
                    total_amount, payment_status, special_instructions,
                    order_type, is_rush, ordered_at, folder_path, download_status,
                    pixfizz_job_id,
                    delivery_method_id, shipping_first_name, shipping_last_name,
                    shipping_address1, shipping_address2, shipping_city,
                    shipping_state, shipping_zip, shipping_country, shipping_method,
                    is_test, harvested_by_store_id, created_at
                ) VALUES (
                    @id, @eid, @srcId, @srcCode,
                    @fname, @lname, @email, @phone,
                    1, 'new', @store,
                    @total, @paid, @notes,
                    @type, @rush, @ordered, @folder, @status,
                    @jobId,
                    @deliveryMethod, @shipFname, @shipLname,
                    @shipAddr1, @shipAddr2, @shipCity,
                    @shipState, @shipZip, @shipCountry, @shipMethod,
                    @isTest, @harvestStore, @createdAt
                )
                """;
            var srcCode = (order.ExternalSource ?? "").ToLowerInvariant();
            var srcId = srcCode == "dakis" ? 2 : srcCode == "dashboard" ? 3 : 1;

            cmd.Parameters.AddWithValue("@id", orderId);
            cmd.Parameters.AddWithValue("@eid", order.ExternalOrderId);
            cmd.Parameters.AddWithValue("@srcId", srcId);
            cmd.Parameters.AddWithValue("@srcCode", srcCode);
            cmd.Parameters.AddWithValue("@fname", order.CustomerFirstName ?? "");
            cmd.Parameters.AddWithValue("@lname", order.CustomerLastName ?? "");
            cmd.Parameters.AddWithValue("@email", order.CustomerEmail ?? "");
            cmd.Parameters.AddWithValue("@phone", order.CustomerPhone ?? "");
            cmd.Parameters.AddWithValue("@store", storeId);
            cmd.Parameters.AddWithValue("@total", order.OrderTotal ?? 0m);
            cmd.Parameters.AddWithValue("@paid", order.Paid ? "paid" : "unpaid");
            cmd.Parameters.AddWithValue("@notes", order.Notes ?? "");
            cmd.Parameters.AddWithValue("@type", order.OrderType ?? "");
            cmd.Parameters.AddWithValue("@rush", order.IsRush ? 1 : 0);
            cmd.Parameters.AddWithValue("@ordered", order.OrderedAt?.ToString("O") ?? DateTime.Now.ToString("O"));
            cmd.Parameters.AddWithValue("@folder", order.FolderPath ?? "");
            cmd.Parameters.AddWithValue("@status", order.DownloadStatus);
            cmd.Parameters.AddWithValue("@jobId", (object?)order.PixfizzJobId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@deliveryMethod", order.DeliveryMethodId);
            cmd.Parameters.AddWithValue("@shipFname", order.ShippingFirstName ?? "");
            cmd.Parameters.AddWithValue("@shipLname", order.ShippingLastName ?? "");
            cmd.Parameters.AddWithValue("@shipAddr1", (object?)order.ShippingAddress1 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@shipAddr2", (object?)order.ShippingAddress2 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@shipCity", (object?)order.ShippingCity ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@shipState", (object?)order.ShippingState ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@shipZip", (object?)order.ShippingZip ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@shipCountry", (object?)order.ShippingCountry ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@shipMethod", (object?)order.ShippingMethod ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@isTest", order.ExternalOrderId.StartsWith("TEST-", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            cmd.Parameters.AddWithValue("@harvestStore", harvestedByStoreId > 0 ? harvestedByStoreId : storeId);
            cmd.Parameters.AddWithValue("@createdAt", order.OrderedAt?.ToString("O") ?? DateTime.Now.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        var sourceCode = (order.ExternalSource ?? "").ToLowerInvariant();
        var fulfillStoreCache = new Dictionary<string, int?>();
        foreach (var item in order.Items)
        {
            int? resolvedFulfillStore = null;
            if (!string.IsNullOrEmpty(item.FulfillmentStore))
            {
                if (!fulfillStoreCache.TryGetValue(item.FulfillmentStore, out resolvedFulfillStore))
                {
                    resolvedFulfillStore = ResolveStoreId(sourceCode, item.FulfillmentStore) ?? storeId;
                    fulfillStoreCache[item.FulfillmentStore] = resolvedFulfillStore;
                }
            }

            InsertItemCore(conn, transaction, orderId, item, isPrinted: false,
                fulfillmentStoreId: resolvedFulfillStore, localStoreId: storeId);
        }

        transaction.Commit();
        return orderId;
    }

    public List<Core.Models.ChannelInfo> GetAllChannels()
    {
        var channels = new List<Core.Models.ChannelInfo>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT routing_key, channel_number, layout_name
            FROM channel_mappings
            WHERE channel_number != 0
            ORDER BY channel_number
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var routingKey = reader.GetString(0);
            var pipeIndex = routingKey.IndexOf('|');
            var sizeLabel = pipeIndex >= 0 ? routingKey[..pipeIndex] : routingKey;
            var mediaType = pipeIndex >= 0 ? routingKey[(pipeIndex + 1)..] : "";

            channels.Add(new Core.Models.ChannelInfo
            {
                ChannelNumber = reader.GetInt32(1),
                SizeLabel = sizeLabel,
                MediaType = mediaType,
                Description = reader.IsDBNull(2) ? "" : reader.GetString(2)
            });
        }
        return channels;
    }

    public void UpdateOrderStatus(string orderId, string statusCode)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE orders SET status_code = @status, updated_at = datetime('now','localtime') WHERE id = @id";
        cmd.Parameters.AddWithValue("@status", statusCode);
        cmd.Parameters.AddWithValue("@id", orderId);
        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
            AlertCollector.Error(AlertCategory.Database,
                $"UpdateOrderStatus: order {orderId} not found",
                orderId: orderId.ToString(),
                detail: $"Attempted: UPDATE orders SET status_code='{statusCode}' WHERE id={orderId}. " +
                        $"Expected: 1 row updated. Found: 0 rows. " +
                        $"Context: statusCode={statusCode}. State: no matching order in SQLite");
    }

    public void SaveChannelMapping(string routingKey, int channelNumber, string? layoutName = null)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO channel_mappings (routing_key, channel_number, layout_name, updated_at)
            VALUES (@key, @channel, @layout, datetime('now','localtime'))
            ON CONFLICT(routing_key) DO UPDATE SET
                channel_number = @channel,
                layout_name = @layout,
                updated_at = datetime('now','localtime')
            """;
        cmd.Parameters.AddWithValue("@key", routingKey);
        cmd.Parameters.AddWithValue("@channel", channelNumber);
        cmd.Parameters.AddWithValue("@layout", (object?)layoutName ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void DeleteChannelMapping(string routingKey)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM channel_mappings WHERE routing_key = @key";
        cmd.Parameters.AddWithValue("@key", routingKey);
        cmd.ExecuteNonQuery();
    }

    public string? GetLayoutName(string routingKey)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT layout_name FROM channel_mappings WHERE routing_key = @key";
        cmd.Parameters.AddWithValue("@key", routingKey);
        var result = cmd.ExecuteScalar();
        return result is string s ? s : null;
    }

    public List<(string Id, string ExternalOrderId, string PixfizzJobId)> GetUnreceivedPixfizzOrders(DateTime cutoff)
    {
        var results = new List<(string, string, string)>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, external_order_id, pixfizz_job_id
            FROM orders
            WHERE source_code = 'pixfizz'
              AND pixfizz_job_id IS NOT NULL
              AND pixfizz_job_id != ''
              AND is_received_pushed = 0
              AND created_at <= @cutoff
            """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return results;
    }

    public void MarkReceivedPushed(string orderId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE orders SET is_received_pushed = 1 WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", orderId);
        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
            AlertCollector.Error(AlertCategory.Database,
                $"MarkReceivedPushed: order {orderId} not found",
                orderId: orderId.ToString(),
                detail: $"Attempted: UPDATE orders SET is_received_pushed=1 WHERE id={orderId}. " +
                        $"Expected: 1 row updated. Found: 0 rows. " +
                        $"Context: marking Pixfizz /received pushed. State: no matching order in SQLite");
    }

    // ═══════════════════════════════════════════════════════════════
    //  TAB QUERIES — display_tab driven (Session 82 redesign)
    //
    //  display_tab: 1=Pending (own), 2=Printed, 3=Pending all stores
    //  Pending  = (tab=1 AND own store) OR tab=3 (shared parents)
    //  Printed  = tab=2 AND own store
    //  Other    = harvested at another store (show everything)
    //
    //  Orders CAN appear on multiple tabs (shared parent on Pending + Other Store).
    // ═══════════════════════════════════════════════════════════════

    public List<OrderRow> LoadPendingOrders(int storeId)
    {
        var results = new List<OrderRow>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = OrderSelectBase + """
            WHERE (o.display_tab = 1 AND o.harvested_by_store_id = @storeId)
               OR o.display_tab = 3
            ORDER BY o.ordered_at DESC
            """;
        cmd.Parameters.AddWithValue("@storeId", storeId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadOrderRow(reader));
        return results;
    }

    public List<OrderRow> LoadPrintedOrders(int storeId)
    {
        var results = new List<OrderRow>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = OrderSelectBase + """
            WHERE o.display_tab = 2
              AND o.harvested_by_store_id = @storeId
            ORDER BY o.ordered_at DESC
            """;
        cmd.Parameters.AddWithValue("@storeId", storeId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadOrderRow(reader));
        return results;
    }

    // Other Store = everything from another store, regardless of display_tab.
    public List<OrderRow> LoadOtherStoreOrders(int storeId)
    {
        var results = new List<OrderRow>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = OrderSelectBase + """
            WHERE o.harvested_by_store_id != @storeId
              AND o.harvested_by_store_id > 0
            ORDER BY o.ordered_at DESC
            """;
        cmd.Parameters.AddWithValue("@storeId", storeId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadOrderRow(reader));
        return results;
    }

    public Dictionary<string, List<ItemRow>> BatchLoadItems(List<string> orderIds)
    {
        var result = new Dictionary<string, List<ItemRow>>();
        if (orderIds.Count == 0) return result;

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();

        var placeholders = string.Join(",", orderIds.Select((_, i) => $"@id{i}"));
        cmd.CommandText = $"""
            SELECT oi.order_id, oi.id, oi.size_label, oi.media_type, oi.quantity,
                   oi.image_filename, oi.image_filepath,
                   oi.is_noritsu, oi.is_local_production, oi.is_printed,
                   oi.options_json, oi.file_status
            FROM order_items oi
            WHERE oi.order_id IN ({placeholders})
            ORDER BY oi.order_id, oi.size_label, oi.media_type
            """;
        for (int i = 0; i < orderIds.Count; i++)
            cmd.Parameters.AddWithValue($"@id{i}", orderIds[i]);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var orderId = reader.GetString(reader.GetOrdinal("order_id"));
            var item = new ItemRow(
                Id: reader.GetString(reader.GetOrdinal("id")),
                SizeLabel: reader.IsDBNull(reader.GetOrdinal("size_label")) ? "" : reader.GetString(reader.GetOrdinal("size_label")),
                MediaType: reader.IsDBNull(reader.GetOrdinal("media_type")) ? "" : reader.GetString(reader.GetOrdinal("media_type")),
                Quantity: reader.GetInt32(reader.GetOrdinal("quantity")),
                ImageFilename: reader.IsDBNull(reader.GetOrdinal("image_filename")) ? "" : reader.GetString(reader.GetOrdinal("image_filename")),
                ImageFilepath: reader.IsDBNull(reader.GetOrdinal("image_filepath")) ? "" : reader.GetString(reader.GetOrdinal("image_filepath")),
                IsNoritsu: reader.GetInt32(reader.GetOrdinal("is_noritsu")) == 1,
                IsLocalProduction: reader.GetInt32(reader.GetOrdinal("is_local_production")) == 1,
                IsPrinted: reader.GetInt32(reader.GetOrdinal("is_printed")) == 1,
                OptionsJson: reader.IsDBNull(reader.GetOrdinal("options_json")) ? "[]" : reader.GetString(reader.GetOrdinal("options_json")),
                FileStatus: reader.GetInt32(reader.GetOrdinal("file_status")));

            if (!result.ContainsKey(orderId))
                result[orderId] = new List<ItemRow>();
            result[orderId].Add(item);
        }
        return result;
    }

    private static OrderRow ReadOrderRow(SqliteDataReader reader)
    {
        return new OrderRow(
            Id: reader.GetString(reader.GetOrdinal("id")),
            ExternalOrderId: reader.GetString(reader.GetOrdinal("external_order_id")),
            SourceCode: reader.IsDBNull(reader.GetOrdinal("source_code")) ? "" : reader.GetString(reader.GetOrdinal("source_code")),
            StatusCode: reader.IsDBNull(reader.GetOrdinal("status_code")) ? "" : reader.GetString(reader.GetOrdinal("status_code")),
            CustomerFirstName: reader.IsDBNull(reader.GetOrdinal("customer_first_name")) ? "" : reader.GetString(reader.GetOrdinal("customer_first_name")),
            CustomerLastName: reader.IsDBNull(reader.GetOrdinal("customer_last_name")) ? "" : reader.GetString(reader.GetOrdinal("customer_last_name")),
            CustomerEmail: reader.IsDBNull(reader.GetOrdinal("customer_email")) ? "" : reader.GetString(reader.GetOrdinal("customer_email")),
            CustomerPhone: reader.IsDBNull(reader.GetOrdinal("customer_phone")) ? "" : reader.GetString(reader.GetOrdinal("customer_phone")),
            OrderedAt: reader.IsDBNull(reader.GetOrdinal("ordered_at")) ? null : reader.GetString(reader.GetOrdinal("ordered_at")),
            TotalAmount: reader.IsDBNull(reader.GetOrdinal("total_amount")) ? 0 : reader.GetDecimal(reader.GetOrdinal("total_amount")),
            IsHeld: reader.GetInt32(reader.GetOrdinal("is_held")) == 1,
            IsTransfer: reader.GetInt32(reader.GetOrdinal("is_transfer")) == 1,
            FolderPath: reader.IsDBNull(reader.GetOrdinal("folder_path")) ? "" : reader.GetString(reader.GetOrdinal("folder_path")),
            SpecialInstructions: reader.IsDBNull(reader.GetOrdinal("special_instructions")) ? "" : reader.GetString(reader.GetOrdinal("special_instructions")),
            DownloadStatus: reader.IsDBNull(reader.GetOrdinal("download_status")) ? "" : reader.GetString(reader.GetOrdinal("download_status")),
            StoreName: reader.IsDBNull(reader.GetOrdinal("store_name")) ? "" : reader.GetString(reader.GetOrdinal("store_name")),
            PrintedAt: reader.IsDBNull(reader.GetOrdinal("printed_at")) ? null : reader.GetString(reader.GetOrdinal("printed_at")),
            CreatedAt: reader.IsDBNull(reader.GetOrdinal("created_at")) ? null : reader.GetString(reader.GetOrdinal("created_at")));
    }

    public void BatchUpdateFileStatus(List<(string ItemId, int Status)> updates)
    {
        if (updates.Count == 0) return;
        using var conn = _db.OpenConnection();
        foreach (var (itemId, status) in updates)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE order_items SET file_status = @status WHERE id = @id";
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@id", itemId);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetHarvestedBy(string orderId, int storeId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE orders SET harvested_by_store_id = @val, updated_at = datetime('now','localtime') WHERE id = @id AND harvested_by_store_id != @val";
        cmd.Parameters.AddWithValue("@val", storeId);
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.ExecuteNonQuery();
    }

    public void LinkChildItemsToParent(string parentOrderId, string childOrderId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE order_items SET source_item_id = (
                SELECT p.id FROM order_items p
                WHERE p.order_id = @parentId
                  AND p.size_label = order_items.size_label
                  AND p.image_filename = order_items.image_filename
                LIMIT 1
            )
            WHERE order_id = @childId AND source_item_id IS NULL
            """;
        cmd.Parameters.AddWithValue("@parentId", parentOrderId);
        cmd.Parameters.AddWithValue("@childId", childOrderId);
        cmd.ExecuteNonQuery();
    }

    public void SetDisplayTab(string orderId, int displayTab)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        // Only update if value differs AND order hasn't been printed — verify re-ingest must not
        // reset a printed order's display_tab back to PendingAllStores (3)
        cmd.CommandText = "UPDATE orders SET display_tab = @tab, updated_at = datetime('now','localtime') WHERE id = @id AND display_tab != @tab AND is_printed = 0";
        cmd.Parameters.AddWithValue("@tab", displayTab);
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.ExecuteNonQuery();
    }

    public void SetOrderPrinted(string orderId, bool printed)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        // When un-printing, preserve PendingAllStores (3) for shared parents — don't reset to Pending (1)
        cmd.CommandText = printed
            ? "UPDATE orders SET is_printed = 1, printed_at = @pat, display_tab = @tab, updated_at = datetime('now','localtime') WHERE id = @id"
            : "UPDATE orders SET is_printed = 0, printed_at = NULL, display_tab = CASE WHEN display_tab = 3 THEN 3 ELSE 1 END, updated_at = datetime('now','localtime') WHERE id = @id";
        if (printed)
        {
            cmd.Parameters.AddWithValue("@pat", DateTime.Now.ToString("o"));
            cmd.Parameters.AddWithValue("@tab", (int)Core.Models.DisplayTab.Printed);
        }
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.ExecuteNonQuery();
    }

    public bool AreAllItemsPrinted(string orderId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM order_items WHERE order_id = @id AND is_printed = 0";
        cmd.Parameters.AddWithValue("@id", orderId);
        return Convert.ToInt32(cmd.ExecuteScalar()) == 0;
    }

    public void SetExternallyModified(string orderId, bool modified)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE orders SET is_externally_modified = @val, updated_at = datetime('now','localtime') WHERE id = @id";
        cmd.Parameters.AddWithValue("@val", modified ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", orderId);
        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
            AlertCollector.Error(AlertCategory.Database,
                $"SetExternallyModified: order {orderId} not found",
                orderId: orderId.ToString(),
                detail: $"Attempted: UPDATE orders SET is_externally_modified={modified} WHERE id={orderId}. " +
                        $"Expected: 1 row updated. Found: 0 rows. " +
                        $"Context: transfer or LabApi edit. State: no matching order in SQLite.");
    }

    public void SetFolderPath(string orderId, string folderPath)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE orders SET folder_path = @path, updated_at = datetime('now','localtime') WHERE id = @id";
        cmd.Parameters.AddWithValue("@path", folderPath);
        cmd.Parameters.AddWithValue("@id", orderId);
        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
            AlertCollector.Error(AlertCategory.Database,
                $"SetFolderPath: order {orderId} not found",
                orderId: orderId.ToString(),
                detail: $"Attempted: UPDATE orders SET folder_path='{folderPath}' WHERE id={orderId}. " +
                        $"Expected: 1 row updated. Found: 0 rows. " +
                        $"Context: transfer file receive. State: no matching order in SQLite.");
    }

    public List<(int Id, string Name)> GetStores()
    {
        var stores = new List<(int, string)>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, short_name FROM stores ORDER BY id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            stores.Add((reader.GetInt32(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
        return stores;
    }

    public int? ResolveStoreId(string source, string externalId)
    {
        if (string.IsNullOrEmpty(externalId)) return null;

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT store_id FROM store_identifiers WHERE source = @source AND external_id = @eid";
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@eid", externalId);
        var result = cmd.ExecuteScalar();
        return result is long id ? (int)id : null;
    }

    public void SetPickupStore(string orderId, int storeId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE orders SET pickup_store_id = @store, updated_at = datetime('now','localtime') WHERE id = @id";
        cmd.Parameters.AddWithValue("@store", storeId);
        cmd.Parameters.AddWithValue("@id", orderId);
        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
            AlertCollector.Error(AlertCategory.Database,
                $"SetPickupStore: order {orderId} not found",
                detail: $"Attempted: UPDATE orders SET pickup_store_id={storeId} WHERE id={orderId}. " +
                        $"Expected: 1 row updated. Found: 0.");
    }

    public HashSet<string> FindOrderIdsBySizeLabel(string search)
    {
        var ids = new HashSet<string>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT order_id FROM order_items WHERE size_label LIKE @search";
        cmd.Parameters.AddWithValue("@search", $"%{search}%");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    public List<(string Id, string ExternalOrderId, string FolderPath, int PickupStoreId)> GetDakisOrders()
    {
        var results = new List<(string, string, string, int)>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, external_order_id, folder_path, pickup_store_id
            FROM orders WHERE source_code = 'dakis'
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.GetInt32(3)));
        }
        return results;
    }

    public void InsertServiceItem(string orderId, string sizeLabel, string? filepath = null)
    {
        var itemId = Guid.NewGuid().ToString();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO order_items (id, order_id, size_label, quantity, is_noritsu, is_local_production,
                                     image_filepath, image_filename, media_type, options_json)
            VALUES (@itemId, @oid, @size, 1, 0, 0, @path, '', '', '[]')
            """;
        cmd.Parameters.AddWithValue("@itemId", itemId);
        cmd.Parameters.AddWithValue("@oid", orderId);
        cmd.Parameters.AddWithValue("@size", sizeLabel);
        cmd.Parameters.AddWithValue("@path", (object?)filepath ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public HashSet<string> GetSupersededOrderIds(List<string> orderIds)
    {
        var result = new HashSet<string>();
        if (orderIds.Count == 0) return result;

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        var placeholders = string.Join(",", orderIds.Select((_, i) => $"@id{i}"));
        cmd.CommandText = $"SELECT DISTINCT parent_order_id FROM order_links WHERE parent_order_id IN ({placeholders})";
        for (int i = 0; i < orderIds.Count; i++)
            cmd.Parameters.AddWithValue($"@id{i}", orderIds[i]);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    public List<(string ParentOrderId, string ChildOrderId, string LinkType)> GetLinksForOrders(List<string> orderIds)
    {
        var results = new List<(string, string, string)>();
        if (orderIds.Count == 0) return results;

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        var placeholders = string.Join(",", orderIds.Select((_, i) => $"@id{i}"));
        cmd.CommandText = $"""
            SELECT parent_order_id, child_order_id, link_type
            FROM order_links
            WHERE parent_order_id IN ({placeholders}) OR child_order_id IN ({placeholders})
            """;
        for (int i = 0; i < orderIds.Count; i++)
            cmd.Parameters.AddWithValue($"@id{i}", orderIds[i]);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return results;
    }

    public void InsertLink(string parentOrderId, string childOrderId, string linkType, string createdBy)
    {
        var linkId = Guid.NewGuid().ToString();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO order_links (id, parent_order_id, child_order_id, link_type, created_by)
            VALUES (@linkId, @parent, @child, @type, @by)
            """;
        cmd.Parameters.AddWithValue("@linkId", linkId);
        cmd.Parameters.AddWithValue("@parent", parentOrderId);
        cmd.Parameters.AddWithValue("@child", childOrderId);
        cmd.Parameters.AddWithValue("@type", linkType);
        cmd.Parameters.AddWithValue("@by", createdBy);
        cmd.ExecuteNonQuery();
    }

    public List<(string ChildOrderId, string LinkType, string CreatedBy, string CreatedAt)> GetChildOrders(string parentOrderId)
    {
        var results = new List<(string, string, string, string)>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT child_order_id, link_type, created_by, created_at
            FROM order_links WHERE parent_order_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", parentOrderId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add((reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3)));
        return results;
    }

    public (string ParentOrderId, string LinkType)? GetParentOrder(string childOrderId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT parent_order_id, link_type
            FROM order_links WHERE child_order_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", childOrderId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetString(0), reader.GetString(1));
    }

    public string CreateAlteration(string sourceOrderId, string alterationType, string reason, string alteredBy,
        int? newPickupStoreId = null, string? newFolderPath = null, List<string>? itemIds = null)
    {
        using var conn = _db.OpenConnection();
        using var transaction = conn.BeginTransaction();

        // Get the source order's external ID — children branch off the source directly.
        // e.g., splitting "12345-WB1" produces "12345-WB1-S1", not "12345-S1".
        string baseExternalId;
        using (var getEid = conn.CreateCommand())
        {
            getEid.Transaction = transaction;
            getEid.CommandText = "SELECT external_order_id FROM orders WHERE id = @id";
            getEid.Parameters.AddWithValue("@id", sourceOrderId);
            baseExternalId = getEid.ExecuteScalar()?.ToString()
                ?? throw new InvalidOperationException($"CreateAlteration: source order {sourceOrderId} not found");
        }

        // Determine the next version number
        var prefix = alterationType switch
        {
            "change" => "C",
            "split" => "S",
            "outlab" => "X",
            "dakis_split" => "W", // store splits use store code, not this path
            _ => "C" // default to change
        };
        int nextVersion;
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.Transaction = transaction;
            countCmd.CommandText = """
                SELECT COUNT(*) FROM orders
                WHERE external_order_id LIKE @pattern
                """;
            countCmd.Parameters.AddWithValue("@pattern", baseExternalId + $"-{prefix}%");
            nextVersion = Convert.ToInt32(countCmd.ExecuteScalar()!) + 1;
        }

        var newExternalId = $"{baseExternalId}-{prefix}{nextVersion}";

        // Copy order from the source
        var newOrderId = Guid.NewGuid().ToString();
        using (var copyCmd = conn.CreateCommand())
        {
            copyCmd.Transaction = transaction;
            copyCmd.CommandText = $"""
                INSERT INTO orders (
                    id, external_order_id, order_source_id, source_code,
                    customer_first_name, customer_last_name, customer_email, customer_phone,
                    order_status_id, status_code, pickup_store_id,
                    total_amount, payment_status, special_instructions,
                    order_type, is_rush, ordered_at, folder_path, download_status,
                    pixfizz_job_id,
                    delivery_method_id, shipping_first_name, shipping_last_name,
                    shipping_address1, shipping_address2, shipping_city,
                    shipping_state, shipping_zip, shipping_country, shipping_method,
                    is_test, harvested_by_store_id
                )
                SELECT
                    @newId, @newEid, order_source_id, source_code,
                    customer_first_name, customer_last_name, customer_email, customer_phone,
                    order_status_id, status_code, {(newPickupStoreId.HasValue ? "@newStore" : "pickup_store_id")},
                    total_amount, payment_status, special_instructions,
                    order_type, is_rush, ordered_at, {(newFolderPath != null ? "@newFolder" : "folder_path")}, download_status,
                    pixfizz_job_id,
                    delivery_method_id, shipping_first_name, shipping_last_name,
                    shipping_address1, shipping_address2, shipping_city,
                    shipping_state, shipping_zip, shipping_country, shipping_method,
                    is_test, {(newPickupStoreId.HasValue ? "@newStore" : "harvested_by_store_id")}
                FROM orders WHERE id = @srcId
                """;
            copyCmd.Parameters.AddWithValue("@newId", newOrderId);
            copyCmd.Parameters.AddWithValue("@newEid", newExternalId);
            copyCmd.Parameters.AddWithValue("@srcId", sourceOrderId);
            if (newPickupStoreId.HasValue)
                copyCmd.Parameters.AddWithValue("@newStore", newPickupStoreId.Value);
            if (newFolderPath != null)
                copyCmd.Parameters.AddWithValue("@newFolder", newFolderPath);
            copyCmd.ExecuteNonQuery();
        }

        // Copy items from the source order (all items, or only selected if itemIds provided)
        // Each copied item gets a new GUID
        using (var readItems = conn.CreateCommand())
        {
            readItems.Transaction = transaction;
            var itemFilter = "WHERE order_id = @srcOid";
            if (itemIds is { Count: > 0 })
            {
                var placeholders = string.Join(", ", itemIds.Select((_, i) => $"@itemId{i}"));
                itemFilter += $" AND id IN ({placeholders})";
            }
            readItems.CommandText = $"""
                INSERT INTO order_items (
                    id, order_id, size_label, media_type, category, sub_category,
                    quantity, image_filename, image_filepath, original_image_filepath,
                    is_noritsu, is_local_production, is_printed, options_json
                )
                SELECT
                    lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)),2) || '-' || substr('89ab',abs(random()) % 4 + 1, 1) || substr(hex(randomblob(2)),2) || '-' || hex(randomblob(6))),
                    @newOid, size_label, media_type, category, sub_category,
                    quantity, image_filename, image_filepath, original_image_filepath,
                    is_noritsu, is_local_production, 0, options_json
                FROM order_items {itemFilter}
                """;
            readItems.Parameters.AddWithValue("@newOid", newOrderId);
            readItems.Parameters.AddWithValue("@srcOid", sourceOrderId);
            if (itemIds is { Count: > 0 })
            {
                for (int i = 0; i < itemIds.Count; i++)
                    readItems.Parameters.AddWithValue($"@itemId{i}", itemIds[i]);
            }
            readItems.ExecuteNonQuery();
        }

        // Mark parent as "dealt with" — only when ALL items sent (no itemIds filter)
        if (itemIds is null or { Count: 0 })
        {
            using var markDone = conn.CreateCommand();
            markDone.Transaction = transaction;
            markDone.CommandText = "UPDATE orders SET is_printed = 1, printed_at = datetime('now','localtime'), updated_at = datetime('now','localtime') WHERE id = @id";
            markDone.Parameters.AddWithValue("@id", sourceOrderId);
            markDone.ExecuteNonQuery();
        }

        // Insert link
        using (var linkCmd = conn.CreateCommand())
        {
            var linkId = Guid.NewGuid().ToString();
            linkCmd.Transaction = transaction;
            linkCmd.CommandText = """
                INSERT INTO order_links (id, parent_order_id, child_order_id, link_type, created_by)
                VALUES (@linkId, @parent, @child, @type, @by)
                """;
            linkCmd.Parameters.AddWithValue("@linkId", linkId);
            linkCmd.Parameters.AddWithValue("@parent", sourceOrderId);
            linkCmd.Parameters.AddWithValue("@child", newOrderId);
            linkCmd.Parameters.AddWithValue("@type", alterationType);
            linkCmd.Parameters.AddWithValue("@by", alteredBy);
            linkCmd.ExecuteNonQuery();
        }

        transaction.Commit();
        return newOrderId;
    }
}
