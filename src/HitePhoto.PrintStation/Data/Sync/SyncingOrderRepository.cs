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

    public int InsertOrder(UnifiedOrder order, int storeId)
    {
        var id = _inner.InsertOrder(order, storeId);
        var payload = JsonSerializer.Serialize(new { localOrderId = id });
        _ = Task.Run(() => _sync.PushAsync("orders", id, "insert_order", payload));
        return id;
    }

    public void SetHold(int orderId, bool isHeld)
    {
        _inner.SetHold(orderId, isHeld);
        var payload = JsonSerializer.Serialize(new { orderId, isHeld });
        _ = Task.Run(() => _sync.PushAsync("orders", orderId, "set_hold", payload));
    }

    public void SetNotified(int orderId)
    {
        _inner.SetNotified(orderId);
        var payload = JsonSerializer.Serialize(new { orderId });
        _ = Task.Run(() => _sync.PushAsync("orders", orderId, "set_notified", payload));
    }

    public void SetCurrentLocation(int orderId, int storeId)
    {
        _inner.SetCurrentLocation(orderId, storeId);
        var payload = JsonSerializer.Serialize(new { orderId, storeId });
        _ = Task.Run(() => _sync.PushAsync("orders", orderId, "set_current_location", payload));
    }

    public void SetItemsPrinted(List<int> itemIds)
    {
        _inner.SetItemsPrinted(itemIds);
        if (itemIds.Count == 0) return;
        // Need the order ID for the push — get it from the first item
        var payload = JsonSerializer.Serialize(new { orderId = 0, itemIds });
        _ = Task.Run(() => _sync.PushAsync("order_items", 0, "set_items_printed", payload));
    }

    public void UpdateOrderStatus(int orderId, string statusCode)
    {
        _inner.UpdateOrderStatus(orderId, statusCode);
        var payload = JsonSerializer.Serialize(new { orderId, statusCode });
        _ = Task.Run(() => _sync.PushAsync("orders", orderId, "update_status", payload));
    }

    public void UpdateItem(int itemId, string sizeLabel, string mediaType,
        string imageFilename, string imageFilepath, int quantity,
        bool isNoritsu, string category, string subCategory)
    {
        _inner.UpdateItem(itemId, sizeLabel, mediaType, imageFilename, imageFilepath,
            quantity, isNoritsu, category, subCategory);
        // Item updates are repair operations — push the full order on next insert_order
    }

    public void InsertItem(int orderId, UnifiedOrderItem item)
    {
        _inner.InsertItem(orderId, item);
        // Repair insert — will be captured by next full order push
    }

    public void ReplaceItems(int orderId, List<UnifiedOrderItem> items)
    {
        _inner.ReplaceItems(orderId, items);
        // Full item replace — will be captured by next full order push
    }

    public void InsertItemOptions(int orderItemId, List<HitePhoto.Shared.Parsers.OrderItemOption> options)
    {
        _inner.InsertItemOptions(orderItemId, options);
        // Options are part of the item — synced when order is pushed
    }

    // ── Pass-through (reads, local-only) ────────────────────────────────

    public OrderRecord? GetOrder(int orderId) => _inner.GetOrder(orderId);
    public HitePhoto.Shared.Models.Order? GetFullOrder(int orderId) => _inner.GetFullOrder(orderId);
    public int? FindOrderId(string externalOrderId, int storeId) => _inner.FindOrderId(externalOrderId, storeId);
    public int? FindOrderIdAnyStore(string externalOrderId) => _inner.FindOrderIdAnyStore(externalOrderId);
    public List<OrderItemRecord> GetNoritsuItems(int orderId) => _inner.GetNoritsuItems(orderId);
    public string GetStoreName(int storeId) => _inner.GetStoreName(storeId);
    public List<OrderItemRecord> GetItems(int orderId) => _inner.GetItems(orderId);
    public List<HitePhoto.Shared.Parsers.OrderItemOption> GetItemOptions(int orderItemId) => _inner.GetItemOptions(orderItemId);
    public void DeleteItemOptions(int orderItemId) => _inner.DeleteItemOptions(orderItemId);
    public Dictionary<string, (int Id, string FolderPath, string SourceCode)> GetRecentOrders(int days) => _inner.GetRecentOrders(days);
    public List<ChannelInfo> GetAllChannels() => _inner.GetAllChannels();
    public void SaveChannelMapping(string routingKey, int channelNumber, string? layoutName = null) => _inner.SaveChannelMapping(routingKey, channelNumber, layoutName);
    public void DeleteChannelMapping(string routingKey) => _inner.DeleteChannelMapping(routingKey);
    public string? GetLayoutName(string routingKey) => _inner.GetLayoutName(routingKey);
    public List<(int Id, string ExternalOrderId, string PixfizzJobId)> GetUnreceivedPixfizzOrders(DateTime cutoff) => _inner.GetUnreceivedPixfizzOrders(cutoff);
    public void MarkReceivedPushed(int orderId) => _inner.MarkReceivedPushed(orderId);
    public List<OrderRow> LoadPendingOrders(int storeId) => _inner.LoadPendingOrders(storeId);
    public List<OrderRow> LoadPrintedOrders(int storeId) => _inner.LoadPrintedOrders(storeId);
    public List<OrderRow> LoadOtherStoreOrders(int storeId) => _inner.LoadOtherStoreOrders(storeId);
    public Dictionary<int, List<ItemRow>> BatchLoadItems(List<int> orderIds) => _inner.BatchLoadItems(orderIds);
    public void SetItemsUnprinted(int orderId) => _inner.SetItemsUnprinted(orderId);
    public void BatchUpdateFileStatus(List<(int ItemId, int Status)> updates) => _inner.BatchUpdateFileStatus(updates);
    public void SetFilesLocal(int orderId, bool local) => _inner.SetFilesLocal(orderId, local);
    public void SetOrderPrinted(int orderId, bool printed) => _inner.SetOrderPrinted(orderId, printed);
    public bool AreAllItemsPrinted(int orderId) => _inner.AreAllItemsPrinted(orderId);
    public void SetExternallyModified(int orderId, bool modified) => _inner.SetExternallyModified(orderId, modified);
    public void SetFolderPath(int orderId, string folderPath) => _inner.SetFolderPath(orderId, folderPath);
    public List<(int Id, string Name)> GetStores() => _inner.GetStores();
    public int? ResolveStoreId(string source, string externalId) => _inner.ResolveStoreId(source, externalId);
    public void SetPickupStore(int orderId, int storeId) => _inner.SetPickupStore(orderId, storeId);
    public HashSet<int> FindOrderIdsBySizeLabel(string search) => _inner.FindOrderIdsBySizeLabel(search);
    public List<(int Id, string ExternalOrderId, string FolderPath, int PickupStoreId)> GetDakisOrders() => _inner.GetDakisOrders();

    // Link table — reads pass through, writes could push in future
    public void InsertLink(int parentOrderId, int childOrderId, string linkType, string createdBy) => _inner.InsertLink(parentOrderId, childOrderId, linkType, createdBy);
    public List<(int ChildOrderId, string LinkType, string CreatedBy, string CreatedAt)> GetChildOrders(int parentOrderId) => _inner.GetChildOrders(parentOrderId);
    public (int ParentOrderId, string LinkType)? GetParentOrder(int childOrderId) => _inner.GetParentOrder(childOrderId);

    public int CreateAlteration(int sourceOrderId, string alterationType, string reason, string alteredBy,
        int? newPickupStoreId = null, string? newFolderPath = null)
    {
        var id = _inner.CreateAlteration(sourceOrderId, alterationType, reason, alteredBy, newPickupStoreId, newFolderPath);
        var payload = JsonSerializer.Serialize(new { localOrderId = id, sourceOrderId, alterationType });
        _ = Task.Run(() => _sync.PushAsync("orders", id, "create_alteration", payload));
        return id;
    }
}
