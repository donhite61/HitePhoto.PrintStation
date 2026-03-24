using System.IO;
using Microsoft.Data.Sqlite;
using HitePhoto.PrintStation.Data;

namespace HitePhoto.PrintStation.Core.Services;

public class TransferService : ITransferService
{
    private readonly OrderDb _db;
    // TODO: SFTP client for file transfer

    public TransferService(OrderDb db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public void TransferOrder(int orderId, int targetStoreId, string operatorName, string comment)
    {
        using var conn = _db.OpenConnection();
        using var transaction = conn.BeginTransaction();

        // Update current location
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE orders
                SET current_location_store_id = @store, updated_at = datetime('now')
                WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("@store", targetStoreId);
            cmd.Parameters.AddWithValue("@id", orderId);
            cmd.ExecuteNonQuery();
        }

        // Write transfer marker file
        WriteTransferMarker(conn, orderId, targetStoreId, operatorName, comment, itemIds: null);

        var storeName = OrderHelpers.GetStoreName(conn, targetStoreId);
        var note = string.IsNullOrEmpty(comment)
            ? $"Transferred to {storeName} by {operatorName}"
            : $"Transferred to {storeName} by {operatorName}: {comment}";
        OrderHelpers.AddHistoryNote(conn, orderId, note, operatorName);

        transaction.Commit();

        // TODO: SFTP files to other store
    }

    public void TransferItems(int orderId, List<int> itemIds, int targetStoreId, string operatorName, string comment)
    {
        using var conn = _db.OpenConnection();
        using var transaction = conn.BeginTransaction();

        // Write transfer marker file listing specific items
        WriteTransferMarker(conn, orderId, targetStoreId, operatorName, comment, itemIds);

        var storeName = OrderHelpers.GetStoreName(conn, targetStoreId);
        var note = $"Transferred {itemIds.Count} item(s) to {storeName} by {operatorName}";
        if (!string.IsNullOrEmpty(comment))
            note += $": {comment}";
        OrderHelpers.AddHistoryNote(conn, orderId, note, operatorName);

        transaction.Commit();

        // TODO: SFTP selected files to other store
    }

    private void WriteTransferMarker(SqliteConnection conn, int orderId, int targetStoreId,
        string operatorName, string comment, List<int>? itemIds)
    {
        // Get order folder path
        string folderPath;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT folder_path FROM orders WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", orderId);
            folderPath = (string?)cmd.ExecuteScalar() ?? "";
        }

        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            return;

        var metadataDir = Path.Combine(folderPath, "metadata");
        Directory.CreateDirectory(metadataDir);

        var storeName = OrderHelpers.GetStoreName(conn, targetStoreId);
        var lines = new List<string>
        {
            $"transferred_at: {DateTime.UtcNow:O}",
            $"transferred_to: {storeName}",
            $"transferred_by: {operatorName}"
        };

        if (!string.IsNullOrEmpty(comment))
            lines.Add($"comment: {comment}");

        if (itemIds is not null)
        {
            // List specific items that were transferred
            foreach (var itemId in itemIds)
            {
                string itemLabel;
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT size_label, media_type FROM order_items WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", itemId);
                using var reader = cmd.ExecuteReader();
                itemLabel = reader.Read()
                    ? $"{reader.GetString(0)} {(reader.IsDBNull(1) ? "" : reader.GetString(1))}".Trim()
                    : $"item {itemId}";
                lines.Add($"item: {itemLabel}");
            }
        }
        else
        {
            lines.Add("items: all");
        }

        File.WriteAllLines(Path.Combine(metadataDir, "transfer.txt"), lines);
    }

}
