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

    /// <summary>
    /// For transferred orders (is_externally_modified=1), check if disk files match DB items.
    /// Returns a list of mismatches. Empty list = all good.
    /// </summary>
    List<TransferMismatch> CheckTransferMismatches(int orderId);

    /// <summary>
    /// Send an order to another store for production. Creates a child work order
    /// linked to the parent, SFTP's files, marks parent as dealt with.
    /// Returns the new work order's ID.
    /// </summary>
    int SendForProduction(int orderId, int targetStoreId, string operatorName, string comment);
}

public record TransferMismatch(string ItemDescription, string Issue);
