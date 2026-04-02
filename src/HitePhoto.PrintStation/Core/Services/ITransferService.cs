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
    int SendForProduction(int orderId, int targetStoreId, string operatorName, string comment,
        List<int>? itemIds = null, List<string>? folderNames = null, bool createOrder = true);

    /// <summary>
    /// Get an order from another store's production folder. Downloads files via SFTP,
    /// creates a -R# child order if createOrder=true.
    /// Returns the new order's ID, or null if createOrder=false.
    /// </summary>
    int? GetFromProduction(int orderId, bool createOrder, string operatorName, string comment,
        List<int>? itemIds = null, List<string>? folderNames = null);

    /// <summary>List subfolder names in a remote order folder (for folder checkbox UI).</summary>
    List<string> ListRemoteFolders(string remotePath);
}

public record TransferMismatch(string ItemDescription, string Issue);
