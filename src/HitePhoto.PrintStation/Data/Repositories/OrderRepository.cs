using System.IO;
using Microsoft.Data.Sqlite;
using HitePhoto.PrintStation.Core.Ingest;
using HitePhoto.PrintStation.Core.Models;

namespace HitePhoto.PrintStation.Data.Repositories;

public class OrderRepository : IOrderRepository
{
    private readonly OrderDb _db;

    public OrderRepository(OrderDb db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public OrderRecord? GetOrder(int orderId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, external_order_id, source_code, pickup_store_id,
                   customer_email, folder_path, is_held
            FROM orders WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", orderId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new OrderRecord(
            Id: reader.GetInt32(0),
            ExternalOrderId: reader.GetString(1),
            Source: OrderSourceExtensions.FromCode(reader.GetString(2)),
            PickupStoreId: reader.GetInt32(3),
            CustomerEmail: reader.IsDBNull(4) ? "" : reader.GetString(4),
            FolderPath: reader.IsDBNull(5) ? "" : reader.GetString(5),
            IsHeld: reader.GetInt32(6) == 1);
    }

    public HitePhoto.Shared.Models.Order? GetFullOrder(int orderId)
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
            Id = reader.GetInt32(0),
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

    public List<OrderItemRecord> GetNoritsuItems(int orderId)
    {
        var items = new List<OrderItemRecord>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, order_id, size_label, media_type, image_filepath,
                   quantity, is_noritsu, is_printed
            FROM order_items
            WHERE order_id = @id AND is_noritsu = 1
            ORDER BY size_label, media_type
            """;
        cmd.Parameters.AddWithValue("@id", orderId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new OrderItemRecord(
                Id: reader.GetInt32(0),
                OrderId: reader.GetInt32(1),
                SizeLabel: reader.GetString(2),
                MediaType: reader.IsDBNull(3) ? "" : reader.GetString(3),
                ImageFilepath: reader.IsDBNull(4) ? "" : reader.GetString(4),
                Quantity: reader.GetInt32(5),
                IsNoritsu: reader.GetInt32(6) == 1,
                IsPrinted: reader.GetInt32(7) == 1));
        }
        return items;
    }

    public void SetHold(int orderId, bool isHeld)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE orders SET is_held = @held, updated_at = datetime('now') WHERE id = @id";
        cmd.Parameters.AddWithValue("@held", isHeld ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.ExecuteNonQuery();
    }

    public void SetNotified(int orderId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE orders SET is_notified = 1, updated_at = datetime('now') WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.ExecuteNonQuery();
    }

    public void SetCurrentLocation(int orderId, int storeId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE orders SET current_location_store_id = @store, updated_at = datetime('now')
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@store", storeId);
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.ExecuteNonQuery();
    }

    public void SetItemsPrinted(List<int> itemIds)
    {
        if (itemIds.Count == 0) return;
        using var conn = _db.OpenConnection();
        foreach (var id in itemIds)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE order_items SET is_printed = 1, updated_at = datetime('now') WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public string GetStoreName(int storeId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT short_name FROM stores WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", storeId);
        return (string?)cmd.ExecuteScalar() ?? $"store {storeId}";
    }

    public int? FindOrderId(string externalOrderId, int storeId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM orders WHERE external_order_id = @eid AND pickup_store_id = @store";
        cmd.Parameters.AddWithValue("@eid", externalOrderId);
        cmd.Parameters.AddWithValue("@store", storeId);
        var result = cmd.ExecuteScalar();
        return result != null ? Convert.ToInt32(result) : null;
    }

    public List<OrderItemRecord> GetItems(int orderId)
    {
        var items = new List<OrderItemRecord>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, order_id, size_label, media_type, image_filepath,
                   quantity, is_noritsu, is_printed, image_filename,
                   category, sub_category
            FROM order_items
            WHERE order_id = @id
            ORDER BY size_label, media_type
            """;
        cmd.Parameters.AddWithValue("@id", orderId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new OrderItemRecord(
                Id: reader.GetInt32(0),
                OrderId: reader.GetInt32(1),
                SizeLabel: reader.IsDBNull(2) ? "" : reader.GetString(2),
                MediaType: reader.IsDBNull(3) ? "" : reader.GetString(3),
                ImageFilepath: reader.IsDBNull(4) ? "" : reader.GetString(4),
                Quantity: reader.GetInt32(5),
                IsNoritsu: reader.GetInt32(6) == 1,
                IsPrinted: reader.GetInt32(7) == 1,
                ImageFilename: reader.IsDBNull(8) ? "" : reader.GetString(8),
                Category: reader.IsDBNull(9) ? "" : reader.GetString(9),
                SubCategory: reader.IsDBNull(10) ? "" : reader.GetString(10)));
        }
        return items;
    }

    public void UpdateItem(int itemId, string sizeLabel, string mediaType,
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
                updated_at = datetime('now')
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
        cmd.ExecuteNonQuery();
    }

    public void InsertItemOptions(int orderItemId, List<HitePhoto.Shared.Parsers.OrderItemOption> options)
    {
        if (options.Count == 0) return;
        using var conn = _db.OpenConnection();
        foreach (var opt in options)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO order_item_options (order_item_id, option_key, option_value)
                VALUES (@itemId, @key, @value)
                """;
            cmd.Parameters.AddWithValue("@itemId", orderItemId);
            cmd.Parameters.AddWithValue("@key", opt.Key);
            cmd.Parameters.AddWithValue("@value", opt.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public List<HitePhoto.Shared.Parsers.OrderItemOption> GetItemOptions(int orderItemId)
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

    public void DeleteItemOptions(int orderItemId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM order_item_options WHERE order_item_id = @id";
        cmd.Parameters.AddWithValue("@id", orderItemId);
        cmd.ExecuteNonQuery();
    }

    public void InsertItem(int orderId, UnifiedOrderItem item)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO order_items (
                order_id, size_label, media_type, category, sub_category,
                quantity, image_filename, image_filepath, is_noritsu, options_json
            ) VALUES (
                @oid, @size, @media, @cat, @subcat,
                @qty, @fname, @fpath, @noritsu, @options
            )
            """;
        cmd.Parameters.AddWithValue("@oid", orderId);
        cmd.Parameters.AddWithValue("@size", item.SizeLabel ?? "");
        cmd.Parameters.AddWithValue("@media", item.MediaType ?? "");
        cmd.Parameters.AddWithValue("@cat", item.Options.FirstOrDefault(o => o.Key == "Category")?.Value ?? "");
        cmd.Parameters.AddWithValue("@subcat", item.Options.FirstOrDefault(o => o.Key == "SubCategory")?.Value ?? "");
        cmd.Parameters.AddWithValue("@qty", item.Quantity);
        cmd.Parameters.AddWithValue("@fname", item.ImageFilename ?? "");
        cmd.Parameters.AddWithValue("@fpath", item.ImageFilepath ?? "");
        cmd.Parameters.AddWithValue("@noritsu", item.IsNoritsu ? 1 : 0);
        cmd.Parameters.AddWithValue("@options", item.Options.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(item.Options)
            : "[]");
        cmd.ExecuteNonQuery();
    }

    public void ReplaceItems(int orderId, List<UnifiedOrderItem> items)
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

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO order_items (
                    order_id, size_label, media_type, category, sub_category,
                    quantity, image_filename, image_filepath, is_noritsu, is_printed, options_json
                ) VALUES (
                    @oid, @size, @media, @cat, @subcat,
                    @qty, @fname, @fpath, @noritsu, @printed, @options
                )
                """;
            cmd.Parameters.AddWithValue("@oid", orderId);
            cmd.Parameters.AddWithValue("@size", item.SizeLabel ?? "");
            cmd.Parameters.AddWithValue("@media", item.MediaType ?? "");
            cmd.Parameters.AddWithValue("@cat", item.Options.FirstOrDefault(o => o.Key == "Category")?.Value ?? "");
            cmd.Parameters.AddWithValue("@subcat", item.Options.FirstOrDefault(o => o.Key == "SubCategory")?.Value ?? "");
            cmd.Parameters.AddWithValue("@qty", item.Quantity);
            cmd.Parameters.AddWithValue("@fname", item.ImageFilename ?? "");
            cmd.Parameters.AddWithValue("@fpath", item.ImageFilepath ?? "");
            cmd.Parameters.AddWithValue("@noritsu", item.IsNoritsu ? 1 : 0);
            cmd.Parameters.AddWithValue("@printed", wasPrinted ? 1 : 0);
            cmd.Parameters.AddWithValue("@options", item.Options.Count > 0
                ? System.Text.Json.JsonSerializer.Serialize(item.Options)
                : "[]");
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public Dictionary<string, (int Id, string FolderPath, string SourceCode)> GetRecentOrders(int storeId, int days)
    {
        var cutoff = days > 0 ? DateTime.Now.AddDays(-days) : DateTime.MinValue;
        var result = new Dictionary<string, (int Id, string FolderPath, string SourceCode)>(StringComparer.OrdinalIgnoreCase);
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT o.id, o.external_order_id, o.folder_path, o.source_code
            FROM orders o
            WHERE o.pickup_store_id = @storeId
              AND (@daysBack = 0 OR o.ordered_at >= @cutoff)
              AND o.is_test = 0
            """;
        cmd.Parameters.AddWithValue("@storeId", storeId);
        cmd.Parameters.AddWithValue("@daysBack", days);
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("yyyy-MM-dd"));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var eid = reader.GetString(1);
            result[eid] = (
                reader.GetInt32(0),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3));
        }
        return result;
    }

    public int InsertOrder(UnifiedOrder order, int storeId)
    {
        using var conn = _db.OpenConnection();
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
                    order_type, is_rush, ordered_at, folder_path, download_status,
                    pixfizz_job_id,
                    delivery_method_id, shipping_first_name, shipping_last_name,
                    shipping_address1, shipping_address2, shipping_city,
                    shipping_state, shipping_zip, shipping_country, shipping_method,
                    is_test
                ) VALUES (
                    @eid, @srcId, @srcCode,
                    @fname, @lname, @email, @phone,
                    1, 'new', @store,
                    @total, @paid, @notes,
                    @type, @rush, @ordered, @folder, @status,
                    @jobId,
                    @deliveryMethod, @shipFname, @shipLname,
                    @shipAddr1, @shipAddr2, @shipCity,
                    @shipState, @shipZip, @shipCountry, @shipMethod,
                    @isTest
                );
                SELECT last_insert_rowid();
                """;
            var srcCode = (order.ExternalSource ?? "").ToLowerInvariant();
            var srcId = srcCode == "dakis" ? 2 : srcCode == "dashboard" ? 3 : 1;

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
            orderId = Convert.ToInt32(cmd.ExecuteScalar()!);
        }

        foreach (var item in order.Items)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO order_items (
                    order_id, size_label, media_type, category, sub_category,
                    quantity, image_filename, image_filepath, original_image_filepath,
                    is_noritsu, options_json
                ) VALUES (
                    @oid, @size, @media, @cat, @subcat,
                    @qty, @fname, @fpath, @orig,
                    @noritsu, @options
                )
                """;
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
            cmd.Parameters.AddWithValue("@options", item.Options.Count > 0
                ? System.Text.Json.JsonSerializer.Serialize(item.Options)
                : "[]");
            cmd.ExecuteNonQuery();
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
            WHERE channel_number > 0
            ORDER BY channel_number
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var routingKey = reader.GetString(0);
            var parts = routingKey.Split('|');
            var sizeLabel = parts.Length > 0 ? parts[0] : "";
            var mediaType = parts.Length > 1 ? parts[1] : "";

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

    public void UpdateOrderStatus(int orderId, string statusCode)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE orders SET status_code = @status, updated_at = datetime('now') WHERE id = @id";
        cmd.Parameters.AddWithValue("@status", statusCode);
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.ExecuteNonQuery();
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

    public List<(int Id, string ExternalOrderId, string PixfizzJobId)> GetUnreceivedPixfizzOrders(DateTime cutoff)
    {
        var results = new List<(int, string, string)>();
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
            results.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        return results;
    }

    public void MarkReceivedPushed(int orderId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE orders SET is_received_pushed = 1 WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.ExecuteNonQuery();
    }
}
