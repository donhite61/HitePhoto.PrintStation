using HitePhoto.PrintStation.Core.Ingest;
using HitePhoto.PrintStation.Core.Models;

namespace HitePhoto.PrintStation.Data.Repositories;

public interface IOrderRepository
{
    OrderRecord? GetOrder(string orderId);
    HitePhoto.Shared.Models.Order? GetFullOrder(string orderId);
    string? FindOrderId(string externalOrderId, int storeId);
    string? FindOrderIdAnyStore(string externalOrderId);

    /// <summary>Find an order by external_order_id LIKE pattern (e.g., "12345-BH%").</summary>
    string? FindOrderIdByPattern(string pattern);
    List<OrderItemRecord> GetNoritsuItems(string orderId);
    void SetHold(string orderId, bool isHeld);
    void SetNotifiedAt(string orderId);
    void SetCurrentLocation(string orderId, int storeId);
    void SetItemsPrinted(List<string> itemIds);
    string? GetOrderIdForItem(string itemId);
    string GetStoreName(int storeId);

    /// <summary>
    /// Insert a new order with items. Returns the new order ID.
    /// Used by both Pixfizz and Dakis ingest.
    /// </summary>
    string InsertOrder(UnifiedOrder order, int storeId, int currentLocationStoreId = 0);

    /// <summary>
    /// Get all items for an order. Used by verify/repair to compare against source files.
    /// </summary>
    List<OrderItemRecord> GetItems(string orderId);

    /// <summary>
    /// Update a single item's source-of-truth fields.
    /// Never touches hold, printed, or history fields.
    /// </summary>
    void UpdateItem(string itemId, string sizeLabel, string mediaType,
        string imageFilename, string imageFilepath, int quantity,
        bool isNoritsu, string category, string subCategory);

    /// <summary>Insert options for an order item.</summary>
    void InsertItemOptions(string orderItemId, List<HitePhoto.Shared.Parsers.OrderItemOption> options);

    /// <summary>Get all options for an order item.</summary>
    List<HitePhoto.Shared.Parsers.OrderItemOption> GetItemOptions(string orderItemId);

    /// <summary>Delete all options for an order item (for repair/replace).</summary>
    void DeleteItemOptions(string orderItemId);

    /// <summary>
    /// Insert a single item into an existing order.
    /// Used by verify/repair when source file has items not in DB.
    /// </summary>
    void InsertItem(string orderId, UnifiedOrderItem item);

    /// <summary>
    /// Delete all items for an order and re-insert from source.
    /// Preserves printed state where possible by matching on size+filename stem.
    /// Channel assignment comes from channel_mappings table, not stored per-item.
    /// </summary>
    void ReplaceItems(string orderId, List<UnifiedOrderItem> items);

    /// <summary>
    /// Get recent orders for a store within a date range.
    /// Used by verify to build the DB side of the two-list reconciliation.
    /// </summary>
    Dictionary<string, (string Id, string FolderPath, string SourceCode)> GetRecentOrders(int days, int storeId);

    /// <summary>
    /// Get all configured channels from the channel_mappings table.
    /// Used by ChangeSizeWindow for the channel dropdown.
    /// </summary>
    List<Core.Models.ChannelInfo> GetAllChannels();

    /// <summary>
    /// Save or update a channel mapping. Upserts by routing_key.
    /// </summary>
    void UpdateOrderStatus(string orderId, string statusCode);
    void SaveChannelMapping(string routingKey, int channelNumber, string? layoutName = null);
    void DeleteChannelMapping(string routingKey);
    string? GetLayoutName(string routingKey);

    /// <summary>
    /// Get Pixfizz orders older than cutoff that have a job_id but haven't been marked received.
    /// </summary>
    List<(string Id, string ExternalOrderId, string PixfizzJobId)> GetUnreceivedPixfizzOrders(DateTime cutoff);

    /// <summary>Mark a Pixfizz order as received-pushed.</summary>
    void MarkReceivedPushed(string orderId);

    /// <summary>Set all items on an order to unprinted.</summary>
    void SetItemsUnprinted(string orderId);

    /// <summary>Batch update file_status on order_items. 0=unchecked, 1=OK, -1=error.</summary>
    void BatchUpdateFileStatus(List<(string ItemId, int Status)> updates);

    /// <summary>Pending tab: located here + not printed. See feedback_tab_query_locked.md.</summary>
    List<OrderRow> LoadPendingOrders(int storeId, bool testMode);

    /// <summary>Printed tab: located here + printed. See feedback_tab_query_locked.md.</summary>
    List<OrderRow> LoadPrintedOrders(int storeId, bool testMode);

    /// <summary>Other Store tab: located at another store. Empty when sync disabled.</summary>
    List<OrderRow> LoadOtherStoreOrders(int storeId, bool testMode);

    /// <summary>Batch-load items for multiple orders. Keyed by order ID.</summary>
    Dictionary<string, List<ItemRow>> BatchLoadItems(List<string> orderIds);

    /// <summary>Set source_item_id on child items by matching size_label + image_filename to parent items.</summary>
    void LinkChildItemsToParent(string parentOrderId, string childOrderId);

    /// <summary>Set display_tab (1=Pending own store, 2=Printed, 3=Pending all stores).</summary>
    void SetDisplayTab(string orderId, int displayTab);

    /// <summary>Set order-level is_printed flag + display_tab.</summary>
    void SetOrderPrinted(string orderId, bool printed);

    /// <summary>Check if all items on an order are printed.</summary>
    bool AreAllItemsPrinted(string orderId);

    /// <summary>Set the is_externally_modified flag (transfer receive or LabApi edit).</summary>
    void SetExternallyModified(string orderId, bool modified);

    /// <summary>Set the local folder_path for an order (after transfer files arrive).</summary>
    void SetFolderPath(string orderId, string folderPath);

    /// <summary>Get all stores from the stores table.</summary>
    List<(int Id, string Name)> GetStores();

    /// <summary>Resolve an external store ID (Dakis "881", Pixfizz slug) to our DB store ID.</summary>
    int? ResolveStoreId(string source, string externalId);

    /// <summary>Update an order's pickup_store_id.</summary>
    void SetPickupStore(string orderId, int storeId);

    /// <summary>Find order IDs that have items matching a size label search.</summary>
    HashSet<string> FindOrderIdsBySizeLabel(string search);

    /// <summary>Get all Dakis orders with their folder paths for repair scans.</summary>
    List<(string Id, string ExternalOrderId, string FolderPath, int PickupStoreId)> GetDakisOrders();

    /// <summary>
    /// Create an alteration of an existing order. Copies the source order
    /// as a new order with "-A#" or "-W#" suffix, marks parent is_printed=1,
    /// inserts order_links row. Returns the new order's ID.
    /// </summary>
    string CreateAlteration(string sourceOrderId, string alterationType, string reason, string alteredBy,
        int? newPickupStoreId = null, string? newFolderPath = null, List<string>? itemIds = null);

    /// <summary>Insert a Transfer service item (for orders with no real printable items).</summary>
    void InsertServiceItem(string orderId, string sizeLabel, string? filepath = null);

    /// <summary>
    /// Rewrites every item's image_filepath on a child order from oldFolder to newFolder.
    /// Used after GetFromProduction so the receiver's R-child points at locally-downloaded files.
    /// </summary>
    void RebaseChildItemPaths(string childOrderId, string oldFolder, string newFolder);

    /// <summary>Get order IDs that have been superseded (have children in order_links).</summary>
    HashSet<string> GetSupersededOrderIds(List<string> orderIds);

    /// <summary>
    /// Get all parent→child links for a batch of order IDs.
    /// Returns links where parent_order_id is in the provided set.
    /// Used by tree builder to nest children under parents.
    /// </summary>
    List<(string ParentOrderId, string ChildOrderId, string LinkType)> GetLinksForOrders(List<string> orderIds);

    /// <summary>Insert a link between parent and child orders.</summary>
    void InsertLink(string parentOrderId, string childOrderId, string linkType, string createdBy);

    /// <summary>Get all child orders for a parent.</summary>
    List<(string ChildOrderId, string LinkType, string CreatedBy, string CreatedAt)> GetChildOrders(string parentOrderId);

    /// <summary>Get the parent order for a child.</summary>
    (string ParentOrderId, string LinkType)? GetParentOrder(string childOrderId);
}

public record OrderRow(
    string Id, string ExternalOrderId, string SourceCode, string StatusCode,
    string CustomerFirstName, string CustomerLastName,
    string CustomerEmail, string CustomerPhone,
    string? OrderedAt, decimal TotalAmount,
    bool IsHeld, bool IsTransfer,
    string FolderPath, string SpecialInstructions, string DownloadStatus,
    string StoreName,
    string? PrintedAt = null, string? NotifiedAt = null, string? CreatedAt = null);

public record ItemRow(
    string Id, string SizeLabel, string MediaType, int Quantity,
    string ImageFilename, string ImageFilepath,
    bool IsNoritsu, bool IsLocalProduction, bool IsPrinted, string OptionsJson,
    int FileStatus = 0);

public record OrderRecord(
    string Id,
    string ExternalOrderId,
    OrderSource Source,
    int PickupStoreId,
    string CustomerEmail,
    string FolderPath,
    bool IsHeld,
    bool IsExternallyModified = false,
    string? PixfizzJobId = null);

public record OrderItemRecord(
    string Id,
    string OrderId,
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
