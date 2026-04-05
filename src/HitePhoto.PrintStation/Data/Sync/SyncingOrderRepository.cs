using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using HitePhoto.PrintStation.Core.Ingest;
using HitePhoto.PrintStation.Core.Models;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Data.Sync;

/// <summary>
/// Decorator around IOrderRepository that fires MariaDB pushes
/// after every mutating SQLite write. Read methods pass through.
/// </summary>
public class SyncingOrderRepository : IOrderRepository
{
    private readonly IOrderRepository _inner;
    private readonly ISyncService _sync;

    public SyncingOrderRepository(IOrderRepository inner, ISyncService sync)
    {
        _inner = inner;
        _sync = sync;
    }

    // ── Decorated (push after write) ────────────────────────────────────

    public string InsertOrder(UnifiedOrder order, int storeId, int harvestedByStoreId = 0)
    {
        var id = _inner.InsertOrder(order, storeId, harvestedByStoreId);
        var payload = JsonSerializer.Serialize(new { localOrderId = id });
        _ = Task.Run(() => _sync.PushAsync("orders", id, "insert_order", payload));
        return id;
    }

    public void SetHold(string orderId, bool isHeld)
    {
        _inner.SetHold(orderId, isHeld);
        var payload = JsonSerializer.Serialize(new { orderId, isHeld });
        _ = Task.Run(() => _sync.PushAsync("orders", orderId, "set_hold", payload));
    }

    public void SetNotified(string orderId)
    {
        _inner.SetNotified(orderId);
        var payload = JsonSerializer.Serialize(new { orderId });
        _ = Task.Run(() => _sync.PushAsync("orders", orderId, "set_notified", payload));
    }

    public void SetCurrentLocation(string orderId, int storeId)
    {
        _inner.SetCurrentLocation(orderId, storeId);
        var payload = JsonSerializer.Serialize(new { orderId, storeId });
        _ = Task.Run(() => _sync.PushAsync("orders", orderId, "set_current_location", payload));
    }

    public string? GetOrderIdForItem(string itemId) => _inner.GetOrderIdForItem(itemId);

    public void SetItemsPrinted(List<string> itemIds)
    {
        _inner.SetItemsPrinted(itemIds);
        if (itemIds.Count == 0) return;
        // Look up the order ID from the first item
        var orderId = _inner.GetOrderIdForItem(itemIds[0]);
        if (string.IsNullOrEmpty(orderId)) return; // orphan items — nothing to push
        var payload = JsonSerializer.Serialize(new { orderId, itemIds });
        _ = Task.Run(() => _sync.PushAsync("order_items", orderId, "set_items_printed", payload));
    }

    public void UpdateOrderStatus(string orderId, string statusCode)
    {
        _inner.UpdateOrderStatus(orderId, statusCode);
        var payload = JsonSerializer.Serialize(new { orderId, statusCode });
        _ = Task.Run(() => _sync.PushAsync("orders", orderId, "update_status", payload));
    }

    public void UpdateItem(string itemId, string sizeLabel, string mediaType,
        string imageFilename, string imageFilepath, int quantity,
        bool isNoritsu, string category, string subCategory)
    {
        _inner.UpdateItem(itemId, sizeLabel, mediaType, imageFilename, imageFilepath,
            quantity, isNoritsu, category, subCategory);
        // Item updates are repair operations — push the full order on next insert_order
    }

    public void InsertItem(string orderId, UnifiedOrderItem item)
    {
        _inner.InsertItem(orderId, item);
        // Repair insert — will be captured by next full order push
    }

    public void ReplaceItems(string orderId, List<UnifiedOrderItem> items)
    {
        _inner.ReplaceItems(orderId, items);
        // Full item replace — will be captured by next full order push
    }

    public void InsertItemOptions(string orderItemId, List<HitePhoto.Shared.Parsers.OrderItemOption> options)
    {
        _inner.InsertItemOptions(orderItemId, options);
        // Options are part of the item — synced when order is pushed
    }

    // ── Pass-through (reads, local-only) ────────────────────────────────

    public OrderRecord? GetOrder(string orderId) => _inner.GetOrder(orderId);
    public HitePhoto.Shared.Models.Order? GetFullOrder(string orderId) => _inner.GetFullOrder(orderId);
    public string? FindOrderId(string externalOrderId, int storeId) => _inner.FindOrderId(externalOrderId, storeId);
    public string? FindOrderIdAnyStore(string externalOrderId) => _inner.FindOrderIdAnyStore(externalOrderId);
    public string? FindOrderIdByPattern(string pattern) => _inner.FindOrderIdByPattern(pattern);
    public List<OrderItemRecord> GetNoritsuItems(string orderId) => _inner.GetNoritsuItems(orderId);
    public string GetStoreName(int storeId) => _inner.GetStoreName(storeId);
    public List<OrderItemRecord> GetItems(string orderId) => _inner.GetItems(orderId);
    public List<HitePhoto.Shared.Parsers.OrderItemOption> GetItemOptions(string orderItemId) => _inner.GetItemOptions(orderItemId);
    public void DeleteItemOptions(string orderItemId) => _inner.DeleteItemOptions(orderItemId);
    public Dictionary<string, (string Id, string FolderPath, string SourceCode)> GetRecentOrders(int days, int storeId) => _inner.GetRecentOrders(days, storeId);
    public List<ChannelInfo> GetAllChannels() => _inner.GetAllChannels();
    public void SaveChannelMapping(string routingKey, int channelNumber, string? layoutName = null) => _inner.SaveChannelMapping(routingKey, channelNumber, layoutName);
    public void DeleteChannelMapping(string routingKey) => _inner.DeleteChannelMapping(routingKey);
    public string? GetLayoutName(string routingKey) => _inner.GetLayoutName(routingKey);
    public List<(string Id, string ExternalOrderId, string PixfizzJobId)> GetUnreceivedPixfizzOrders(DateTime cutoff) => _inner.GetUnreceivedPixfizzOrders(cutoff);
    public void MarkReceivedPushed(string orderId) => _inner.MarkReceivedPushed(orderId);
    public List<OrderRow> LoadPendingOrders(int storeId) => _inner.LoadPendingOrders(storeId);
    public List<OrderRow> LoadPrintedOrders(int storeId) => _inner.LoadPrintedOrders(storeId);
    public List<OrderRow> LoadOtherStoreOrders(int storeId) => _inner.LoadOtherStoreOrders(storeId);
    public Dictionary<string, List<ItemRow>> BatchLoadItems(List<string> orderIds) => _inner.BatchLoadItems(orderIds);
    public void SetItemsUnprinted(string orderId) => _inner.SetItemsUnprinted(orderId);
    public void BatchUpdateFileStatus(List<(string ItemId, int Status)> updates) => _inner.BatchUpdateFileStatus(updates);
    public void SetHarvestedBy(string orderId, int storeId) => _inner.SetHarvestedBy(orderId, storeId);
    public void LinkChildItemsToParent(string parentOrderId, string childOrderId) => _inner.LinkChildItemsToParent(parentOrderId, childOrderId);

    public void SetDisplayTab(string orderId, int displayTab)
    {
        _inner.SetDisplayTab(orderId, displayTab);
        var payload = JsonSerializer.Serialize(new { orderId, displayTab });
        _ = Task.Run(() => _sync.PushAsync("orders", orderId, "set_display_tab", payload));
    }
    public void SetOrderPrinted(string orderId, bool printed)
    {
        _inner.SetOrderPrinted(orderId, printed);
        var payload = JsonSerializer.Serialize(new { orderId, printed });
        _ = Task.Run(() => _sync.PushAsync("orders", orderId, "set_printed", payload));
    }
    public bool AreAllItemsPrinted(string orderId) => _inner.AreAllItemsPrinted(orderId);
    public void SetExternallyModified(string orderId, bool modified) => _inner.SetExternallyModified(orderId, modified);
    public void SetFolderPath(string orderId, string folderPath) => _inner.SetFolderPath(orderId, folderPath);
    public List<(int Id, string Name)> GetStores() => _inner.GetStores();
    public int? ResolveStoreId(string source, string externalId) => _inner.ResolveStoreId(source, externalId);
    public void SetPickupStore(string orderId, int storeId) => _inner.SetPickupStore(orderId, storeId);
    public HashSet<string> FindOrderIdsBySizeLabel(string search) => _inner.FindOrderIdsBySizeLabel(search);
    public List<(string Id, string ExternalOrderId, string FolderPath, int PickupStoreId)> GetDakisOrders() => _inner.GetDakisOrders();

    public void InsertServiceItem(string orderId, string sizeLabel, string? filepath = null) => _inner.InsertServiceItem(orderId, sizeLabel, filepath);

    // Link table — reads pass through, writes push to sync
    public HashSet<string> GetSupersededOrderIds(List<string> orderIds) => _inner.GetSupersededOrderIds(orderIds);
    public List<(string ParentOrderId, string ChildOrderId, string LinkType)> GetLinksForOrders(List<string> orderIds) => _inner.GetLinksForOrders(orderIds);
    public void InsertLink(string parentOrderId, string childOrderId, string linkType, string createdBy)
    {
        _inner.InsertLink(parentOrderId, childOrderId, linkType, createdBy);
        var payload = JsonSerializer.Serialize(new { parentOrderId, childOrderId, linkType, createdBy });
        _ = Task.Run(() => _sync.PushAsync("order_links", parentOrderId, "insert_link", payload));
    }
    public List<(string ChildOrderId, string LinkType, string CreatedBy, string CreatedAt)> GetChildOrders(string parentOrderId) => _inner.GetChildOrders(parentOrderId);
    public (string ParentOrderId, string LinkType)? GetParentOrder(string childOrderId) => _inner.GetParentOrder(childOrderId);

    public string CreateAlteration(string sourceOrderId, string alterationType, string reason, string alteredBy,
        int? newPickupStoreId = null, string? newFolderPath = null, List<string>? itemIds = null)
    {
        var id = _inner.CreateAlteration(sourceOrderId, alterationType, reason, alteredBy, newPickupStoreId, newFolderPath, itemIds);
        var payload = JsonSerializer.Serialize(new { localOrderId = id, sourceOrderId, alterationType });
        _ = Task.Run(() => _sync.PushAsync("orders", id, "create_alteration", payload));
        return id;
    }
}
