using HitePhoto.PrintStation.Core.Ingest;
using HitePhoto.PrintStation.Core.Models;

namespace HitePhoto.PrintStation.Data.Repositories;

public interface IOrderRepository
{
    OrderRecord? GetOrder(int orderId);
    int? FindOrderId(string externalOrderId, int storeId);
    List<OrderItemRecord> GetNoritsuItems(int orderId);
    void SetHold(int orderId, bool isHeld);
    void SetNotified(int orderId);
    void SetCurrentLocation(int orderId, int storeId);
    void SetItemsPrinted(List<int> itemIds);
    string GetStoreName(int storeId);

    /// <summary>
    /// Insert a new order with items. Returns the new order ID.
    /// Used by both Pixfizz and Dakis ingest.
    /// </summary>
    int InsertOrder(UnifiedOrder order, int storeId);

    /// <summary>
    /// Get all items for an order. Used by verify/repair to compare against source files.
    /// </summary>
    List<OrderItemRecord> GetItems(int orderId);

    /// <summary>
    /// Update a single item's source-of-truth fields (size, media, filename, filepath, quantity).
    /// Never touches hold, printed, or history fields.
    /// </summary>
    void UpdateItem(int itemId, string sizeLabel, string mediaType,
        string imageFilename, string imageFilepath, int quantity);

    /// <summary>
    /// Insert a single item into an existing order.
    /// Used by verify/repair when source file has items not in DB.
    /// </summary>
    void InsertItem(int orderId, UnifiedOrderItem item);

    /// <summary>
    /// Get recent orders for a store within a date range.
    /// Used by verify to build the DB side of the two-list reconciliation.
    /// </summary>
    Dictionary<string, (int Id, string FolderPath, string SourceCode)> GetRecentOrders(int storeId, int days);
}

public record OrderRecord(
    int Id,
    string ExternalOrderId,
    OrderSource Source,
    int PickupStoreId,
    string CustomerEmail,
    string FolderPath,
    bool IsHeld);

public record OrderItemRecord(
    int Id,
    int OrderId,
    string SizeLabel,
    string MediaType,
    string ImageFilepath,
    int Quantity,
    bool IsNoritsu,
    bool IsPrinted,
    string ImageFilename = "");
