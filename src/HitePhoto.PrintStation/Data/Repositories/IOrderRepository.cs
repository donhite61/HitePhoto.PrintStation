using HitePhoto.PrintStation.Core.Ingest;
using HitePhoto.PrintStation.Core.Models;

namespace HitePhoto.PrintStation.Data.Repositories;

public interface IOrderRepository
{
    OrderRecord? GetOrder(int orderId);
    HitePhoto.Shared.Models.Order? GetFullOrder(int orderId);
    int? FindOrderId(string externalOrderId, int storeId);
    int? FindOrderIdAnyStore(string externalOrderId);
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
    /// Delete all items for an order and re-insert from source.
    /// Preserves printed state where possible by matching on size+filename stem.
    /// Channel assignment comes from channel_mappings table, not stored per-item.
    /// </summary>
    void ReplaceItems(int orderId, List<UnifiedOrderItem> items);

    /// <summary>
    /// Get recent orders for a store within a date range.
    /// Used by verify to build the DB side of the two-list reconciliation.
    /// </summary>
    Dictionary<string, (int Id, string FolderPath, string SourceCode)> GetRecentOrders(int days);

    /// <summary>
    /// Get all configured channels from the channel_mappings table.
    /// Used by ChangeSizeWindow for the channel dropdown.
    /// </summary>
    List<Core.Models.ChannelInfo> GetAllChannels();

    /// <summary>
    /// Save or update a channel mapping. Upserts by routing_key.
    /// </summary>
    void UpdateOrderStatus(int orderId, string statusCode);
    void SaveChannelMapping(string routingKey, int channelNumber, string? layoutName = null);
    void DeleteChannelMapping(string routingKey);
    string? GetLayoutName(string routingKey);

    /// <summary>
    /// Get Pixfizz orders older than cutoff that have a job_id but haven't been marked received.
    /// </summary>
    List<(int Id, string ExternalOrderId, string PixfizzJobId)> GetUnreceivedPixfizzOrders(DateTime cutoff);

    /// <summary>Mark a Pixfizz order as received-pushed.</summary>
    void MarkReceivedPushed(int orderId);

    /// <summary>Set all items on an order to unprinted.</summary>
    void SetItemsUnprinted(int orderId);

    /// <summary>Batch update file_status on order_items. 0=unchecked, 1=OK, -1=error.</summary>
    void BatchUpdateFileStatus(List<(int ItemId, int Status)> updates);

    /// <summary>Pending tab: is_printed=0 ONLY. Do not add files_local, status_code, or item subqueries.</summary>
    List<OrderRow> LoadPendingOrders(int storeId);

    /// <summary>Printed tab: is_printed=1 ONLY. Do not add files_local, status_code, or item subqueries.</summary>
    List<OrderRow> LoadPrintedOrders(int storeId);

    /// <summary>Other Store tab: pickup_store_id != storeId ONLY. Do not add files_local or status_code.</summary>
    List<OrderRow> LoadOtherStoreOrders(int storeId);

    /// <summary>Batch-load items for multiple orders. Keyed by order ID.</summary>
    Dictionary<int, List<ItemRow>> BatchLoadItems(List<int> orderIds);

    /// <summary>Set files_local flag (1 = image files exist on this machine's disk).</summary>
    void SetFilesLocal(int orderId, bool local);

    /// <summary>Set order-level is_printed flag (drives Pending vs Printed tab).</summary>
    void SetOrderPrinted(int orderId, bool printed);

    /// <summary>Check if all items on an order are printed.</summary>
    bool AreAllItemsPrinted(int orderId);

    /// <summary>Set the is_externally_modified flag (transfer receive or LabApi edit).</summary>
    void SetExternallyModified(int orderId, bool modified);

    /// <summary>Set the local folder_path for an order (after transfer files arrive).</summary>
    void SetFolderPath(int orderId, string folderPath);

    /// <summary>Get all stores from the stores table.</summary>
    List<(int Id, string Name)> GetStores();

    /// <summary>Resolve an external store ID (Dakis "881", Pixfizz slug) to our DB store ID.</summary>
    int? ResolveStoreId(string source, string externalId);

    /// <summary>Update an order's pickup_store_id.</summary>
    void SetPickupStore(int orderId, int storeId);

    /// <summary>Find order IDs that have items matching a size label search.</summary>
    HashSet<int> FindOrderIdsBySizeLabel(string search);

    /// <summary>Get all Dakis orders with their folder paths for repair scans.</summary>
    List<(int Id, string ExternalOrderId, string FolderPath, int PickupStoreId)> GetDakisOrders();

    /// <summary>
    /// Create an alteration of an existing order. Copies the source order
    /// as a new order with "-A#" or "-W#" suffix, marks parent is_printed=1,
    /// inserts order_links row. Returns the new order's ID.
    /// </summary>
    int CreateAlteration(int sourceOrderId, string alterationType, string reason, string alteredBy,
        int? newPickupStoreId = null, string? newFolderPath = null);

    /// <summary>Insert a link between parent and child orders.</summary>
    void InsertLink(int parentOrderId, int childOrderId, string linkType, string createdBy);

    /// <summary>Get all child orders for a parent.</summary>
    List<(int ChildOrderId, string LinkType, string CreatedBy, string CreatedAt)> GetChildOrders(int parentOrderId);

    /// <summary>Get the parent order for a child.</summary>
    (int ParentOrderId, string LinkType)? GetParentOrder(int childOrderId);
}

public record OrderRow(
    int Id, string ExternalOrderId, string SourceCode, string StatusCode,
    string CustomerFirstName, string CustomerLastName,
    string CustomerEmail, string CustomerPhone,
    string? OrderedAt, decimal TotalAmount,
    bool IsHeld, bool IsTransfer,
    string FolderPath, string SpecialInstructions, string DownloadStatus,
    string StoreName,
    string? Supersedes = null, string? AlterationType = null);

public record ItemRow(
    int Id, string SizeLabel, string MediaType, int Quantity,
    string ImageFilename, string ImageFilepath,
    bool IsNoritsu, bool IsLocalProduction, bool IsPrinted, string OptionsJson,
    int FileStatus = 0);

public record OrderRecord(
    int Id,
    string ExternalOrderId,
    OrderSource Source,
    int PickupStoreId,
    string CustomerEmail,
    string FolderPath,
    bool IsHeld,
    bool IsExternallyModified = false);

public record OrderItemRecord(
    int Id,
    int OrderId,
    string SizeLabel,
    string MediaType,
    string ImageFilepath,
    int Quantity,
    bool IsNoritsu,
    bool IsLocalProduction,
    bool IsPrinted,
    string ImageFilename = "",
    string Category = "",
    string SubCategory = "",
    string OptionsJson = "[]");
