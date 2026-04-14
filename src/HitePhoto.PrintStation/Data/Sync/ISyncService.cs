using System.Threading.Tasks;

namespace HitePhoto.PrintStation.Data.Sync;

public interface ISyncService
{
    /// <summary>
    /// Push a single change to MariaDB. Returns true if pushed successfully,
    /// false if queued in sync_outbox for retry.
    /// Only pushes insert_order if the order's current_location_store_id matches settings.StoreId.
    /// </summary>
    Task<bool> PushAsync(string tableName, string recordId, string operation, string payloadJson);

    /// <summary>
    /// Pull changes from MariaDB newer than last sync. Inserts/updates into local SQLite.
    /// Skips orders that have pending outbox entries.
    /// </summary>
    Task PullAsync();

    /// <summary>Process all pending items in sync_outbox.</summary>
    Task ProcessOutboxAsync();

    /// <summary>Test MariaDB connectivity.</summary>
    Task<bool> IsReachableAsync();
}
