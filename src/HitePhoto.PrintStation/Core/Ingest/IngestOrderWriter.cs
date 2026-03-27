using HitePhoto.PrintStation.Core.Services;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Shared write-to-SQLite logic for all ingest pipelines.
/// FindOrderId → InsertOrder + history note → VerifyOrder.
/// </summary>
public class IngestOrderWriter
{
    private readonly IOrderRepository _orders;
    private readonly IHistoryRepository _history;
    private readonly IOrderVerifier _verifier;

    public IngestOrderWriter(
        IOrderRepository orders,
        IHistoryRepository history,
        IOrderVerifier verifier)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }

    /// <summary>
    /// Insert order if new, then verify. If already exists, just re-verify.
    /// Validates all fields before writing — bad data stops here, not downstream.
    /// </summary>
    public void WriteToSqlite(UnifiedOrder order, int storeId, string sourceCode, string folderPath)
    {
        ValidateOrder(order, storeId, sourceCode);
        ValidateItems(order, sourceCode);

        var existingId = _orders.FindOrderId(order.ExternalOrderId, storeId);

        if (existingId == null)
        {
            var orderId = _orders.InsertOrder(order, storeId);
            if (orderId <= 0)
            {
                AlertCollector.Error(AlertCategory.Database,
                    $"InsertOrder returned invalid ID for {sourceCode} order {order.ExternalOrderId}",
                    orderId: order.ExternalOrderId,
                    detail: $"Attempted: InsertOrder. Expected: positive integer ID. " +
                            $"Found: {orderId}. Context: store {storeId}, {order.Items.Count} items. " +
                            $"State: order may not have been saved.");
                throw new InvalidOperationException($"InsertOrder returned {orderId} for {order.ExternalOrderId}");
            }

            _history.AddNote(orderId, $"Order received at {DateTime.Now:g}");
            AppLog.Info($"Inserted {sourceCode} order {order.ExternalOrderId} (id={orderId}, {order.Items.Count} items)");
        }
        else
        {
            _verifier.VerifyOrder(order.ExternalOrderId, folderPath, sourceCode, existingId.Value);
        }
    }

    // ── Order-level validation ────────────────────────────────────────────

    private static void ValidateOrder(UnifiedOrder order, int storeId, string sourceCode)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(order.ExternalOrderId))
            errors.Add("ExternalOrderId is empty");
        if (string.IsNullOrWhiteSpace(order.ExternalSource))
            errors.Add("ExternalSource is empty");
        if (order.Items.Count == 0 && !order.IsInvoiceOnly)
            errors.Add("zero items (not invoice-only)");
        if (string.IsNullOrWhiteSpace(order.FolderPath))
            errors.Add("FolderPath is empty");
        if (!order.OrderedAt.HasValue)
            errors.Add("OrderedAt is null");
        if (string.IsNullOrWhiteSpace(order.CustomerFirstName) && string.IsNullOrWhiteSpace(order.CustomerLastName))
            errors.Add("no customer name (first and last both empty)");

        if (errors.Count > 0)
        {
            var errorList = string.Join("; ", errors);
            AlertCollector.Error(AlertCategory.DataQuality,
                $"{sourceCode} order {order.ExternalOrderId ?? "(no ID)"} failed validation",
                orderId: order.ExternalOrderId,
                detail: $"Attempted: validate order before SQLite write. " +
                        $"Expected: all required fields populated. " +
                        $"Found: {errorList}. " +
                        $"Context: store {storeId}, source {sourceCode}. " +
                        $"State: order rejected — will not be inserted.");
            throw new InvalidOperationException(
                $"{sourceCode} order {order.ExternalOrderId ?? "(no ID)"} failed validation: {errorList}");
        }
    }

    // ── Item-level validation ─────────────────────────────────────────────

    private static void ValidateItems(UnifiedOrder order, string sourceCode)
    {
        if (order.IsInvoiceOnly) return;

        for (int i = 0; i < order.Items.Count; i++)
        {
            var item = order.Items[i];
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(item.SizeLabel))
                errors.Add("SizeLabel is empty");
            if (item.Quantity <= 0)
                errors.Add($"Quantity is {item.Quantity}");

            // Noritsu (print) items must have image files.
            // Non-Noritsu (gift) items may not have rendered files on disk yet.
            if (item.IsNoritsu)
            {
                if (string.IsNullOrWhiteSpace(item.ImageFilename))
                    errors.Add("ImageFilename is empty");
                if (string.IsNullOrWhiteSpace(item.ImageFilepath))
                    errors.Add("ImageFilepath is empty");
            }

            if (errors.Count > 0)
            {
                var errorList = string.Join("; ", errors);
                AlertCollector.Error(AlertCategory.DataQuality,
                    $"{sourceCode} order {order.ExternalOrderId} item [{i}] failed validation",
                    orderId: order.ExternalOrderId,
                    detail: $"Attempted: validate item before SQLite write. " +
                            $"Expected: SizeLabel, Quantity>0" +
                            (item.IsNoritsu ? ", ImageFilename, ImageFilepath" : "") + " populated. " +
                            $"Found: {errorList}. " +
                            $"Context: item {i} of {order.Items.Count}, IsNoritsu={item.IsNoritsu}, " +
                            $"file '{item.ImageFilename ?? "(null)"}'. " +
                            $"State: entire order rejected — no partial inserts.");
                throw new InvalidOperationException(
                    $"{sourceCode} order {order.ExternalOrderId} item [{i}] failed validation: {errorList}");
            }
        }
    }
}
