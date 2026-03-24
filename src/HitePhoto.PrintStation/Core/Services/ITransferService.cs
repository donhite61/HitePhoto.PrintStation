namespace HitePhoto.PrintStation.Core.Services;

/// <summary>
/// Transfers whole orders or individual items to the other store via SFTP.
/// Updates current_location_store_id. Writes transfer marker file.
/// </summary>
public interface ITransferService
{
    /// <summary>Transfer an entire order to another store.</summary>
    void TransferOrder(int orderId, int targetStoreId, string operatorName, string comment);

    /// <summary>Transfer specific items to another store.</summary>
    void TransferItems(int orderId, List<int> itemIds, int targetStoreId, string operatorName, string comment);
}
