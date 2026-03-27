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
    /// Update a single item's source-of-truth fields.
    /// Never touches hold, printed, or history fields.
    /// </summary>
    void UpdateItem(int itemId, string sizeLabel, string mediaType,
        string imageFilename, string imageFilepath, int quantity,
        bool isNoritsu, string category, string subCategory);

    /// <summary>Insert options for an order item.</summary>
    void InsertItemOptions(int orderItemId, List<HitePhoto.Shared.Parsers.OrderItemOption> options);

    /// <summary>Get all options for an order item.</summary>
    List<HitePhoto.Shared.Parsers.OrderItemOption> GetItemOptions(int orderItemId);

    /// <summary>Delete all options for an order item (for repair/replace).</summary>
    void DeleteItemOptions(int orderItemId);

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

    /// <summary>
    /// Get all configured channels from the channel_mappings table.
    /// Used by ChangeSizeWindow for the channel dropdown.
    /// </summary>
    List<Core.Models.ChannelInfo> GetAllChannels();

    /// <summary>
    /// Save or update a channel mapping. Upserts by routing_key.
    /// </summary>
    void SaveChannelMapping(string routingKey, int channelNumber);
    void DeleteChannelMapping(string routingKey);
    string? GetLayoutName(string routingKey);

    /// <summary>
    /// Update channel_number on all order_items matching this size+media.
    /// Called after assigning a channel mapping so existing orders reflect the change.
    /// </summary>
    void UpdateItemChannels(string sizeLabel, string mediaType, int channelNumber);

    /// <summary>
    /// Get Pixfizz orders older than cutoff that have a job_id but haven't been marked received.
    /// </summary>
    List<(int Id, string ExternalOrderId, string PixfizzJobId)> GetUnreceivedPixfizzOrders(DateTime cutoff);

    /// <summary>Mark a Pixfizz order as received-pushed.</summary>
    void MarkReceivedPushed(int orderId);
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
    string ImageFilename = "",
    string Category = "",
    string SubCategory = "");
