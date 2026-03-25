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
                   quantity, is_noritsu, is_printed, image_filename
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
                ImageFilename: reader.IsDBNull(8) ? "" : reader.GetString(8)));
        }
        return items;
    }

    public void UpdateItem(int itemId, string sizeLabel, string mediaType,
        string imageFilename, string imageFilepath, int quantity)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE order_items SET
                size_label = @size, media_type = @media,
                image_filename = @fname, image_filepath = @fpath,
                quantity = @qty, updated_at = datetime('now')
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@size", sizeLabel);
        cmd.Parameters.AddWithValue("@media", mediaType);
        cmd.Parameters.AddWithValue("@fname", imageFilename);
        cmd.Parameters.AddWithValue("@fpath", imageFilepath);
        cmd.Parameters.AddWithValue("@qty", quantity);
        cmd.Parameters.AddWithValue("@id", itemId);
        cmd.ExecuteNonQuery();
    }

    public void InsertItem(int orderId, UnifiedOrderItem item)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO order_items (
                order_id, size_label, media_type, quantity,
                image_filename, image_filepath, is_noritsu, options_json
            ) VALUES (
                @oid, @size, @media, @qty,
                @fname, @fpath, @noritsu, @options
            )
            """;
        cmd.Parameters.AddWithValue("@oid", orderId);
        cmd.Parameters.AddWithValue("@size", item.SizeLabel ?? "");
        cmd.Parameters.AddWithValue("@media", item.MediaType ?? "");
        cmd.Parameters.AddWithValue("@qty", item.Quantity);
        cmd.Parameters.AddWithValue("@fname", item.ImageFilename ?? "");
        cmd.Parameters.AddWithValue("@fpath", item.ImageFilepath ?? "");
        cmd.Parameters.AddWithValue("@noritsu", item.IsNoritsu ? 1 : 0);
        cmd.Parameters.AddWithValue("@options", item.Options.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(item.Options)
            : "[]");
        cmd.ExecuteNonQuery();
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
                    order_type, is_rush, ordered_at, folder_path, download_status
                ) VALUES (
                    @eid, @srcId, @srcCode,
                    @fname, @lname, @email, @phone,
                    1, 'new', @store,
                    @total, @paid, @notes,
                    @type, @rush, @ordered, @folder, @status
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

        transaction.Commit();
        return orderId;
    }
}
