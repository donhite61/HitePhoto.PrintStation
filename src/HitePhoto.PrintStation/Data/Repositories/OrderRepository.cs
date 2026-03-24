using Microsoft.Data.Sqlite;
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
}
