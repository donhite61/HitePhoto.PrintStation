using System.IO;
using Microsoft.Data.Sqlite;
using HitePhoto.PrintStation.Data;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core.Services;

public class TransferService : ITransferService
{
    private readonly IOrderRepository _orders;
    private readonly IHistoryRepository _history;

    public TransferService(IOrderRepository orders, IHistoryRepository history)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }

    public void TransferOrder(int orderId, int targetStoreId, string operatorName, string comment)
    {
        var order = _orders.GetOrder(orderId);
        if (order is null)
            throw new InvalidOperationException($"Order {orderId} not found.");

        _orders.SetCurrentLocation(orderId, targetStoreId);

        WriteTransferMarker(order.FolderPath, targetStoreId, operatorName, comment, itemIds: null);

        var storeName = _orders.GetStoreName(targetStoreId);
        var note = string.IsNullOrEmpty(comment)
            ? $"Transferred to {storeName} by {operatorName}"
            : $"Transferred to {storeName} by {operatorName}: {comment}";
        _history.AddNote(orderId, note, operatorName);

        // TODO: SFTP files to other store
    }

    public void TransferItems(int orderId, List<int> itemIds, int targetStoreId, string operatorName, string comment)
    {
        var order = _orders.GetOrder(orderId);
        if (order is null)
            throw new InvalidOperationException($"Order {orderId} not found.");

        WriteTransferMarker(order.FolderPath, targetStoreId, operatorName, comment, itemIds);

        var storeName = _orders.GetStoreName(targetStoreId);
        var note = $"Transferred {itemIds.Count} item(s) to {storeName} by {operatorName}";
        if (!string.IsNullOrEmpty(comment))
            note += $": {comment}";
        _history.AddNote(orderId, note, operatorName);

        // TODO: SFTP selected files to other store
    }

    private void WriteTransferMarker(string folderPath, int targetStoreId,
        string operatorName, string comment, List<int>? itemIds)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            return;

        var metadataDir = Path.Combine(folderPath, "metadata");
        Directory.CreateDirectory(metadataDir);

        var storeName = _orders.GetStoreName(targetStoreId);
        var lines = new List<string>
        {
            $"transferred_at: {DateTime.UtcNow:O}",
            $"transferred_to: {storeName}",
            $"transferred_by: {operatorName}"
        };

        if (!string.IsNullOrEmpty(comment))
            lines.Add($"comment: {comment}");

        if (itemIds is not null)
            lines.Add($"items: {itemIds.Count} specific items");
        else
            lines.Add("items: all");

        File.WriteAllLines(Path.Combine(metadataDir, "transfer.txt"), lines);
    }
}
