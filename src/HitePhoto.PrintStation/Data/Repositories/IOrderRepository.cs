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
    bool IsPrinted);
