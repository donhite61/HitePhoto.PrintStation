using System.IO;
using HitePhoto.Shared.Parsers;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Parses a Pixfizz darkroom_ticket.txt into a complete UnifiedOrder.
/// TXT is the source of truth for all order content: customer, sizes, quantities, filenames.
/// File paths are calculated from the expected prints/ folder structure, not scanned from disk.
/// </summary>
public class PixfizzOrderParser
{
    /// <summary>
    /// Parse a Pixfizz order from TXT content.
    /// raw.RawData = darkroom_ticket.txt content
    /// raw.Metadata["folder_path"] = order folder on disk
    /// raw.Metadata["job_id"] = optional Pixfizz job ID
    /// </summary>
    public UnifiedOrder Parse(RawOrder raw)
    {
        var txtContent = raw.RawData;
        if (string.IsNullOrWhiteSpace(txtContent))
        {
            AlertCollector.Error(AlertCategory.DataQuality,
                "Pixfizz TXT content is empty",
                orderId: raw.ExternalOrderId,
                detail: $"Attempted: parse darkroom_ticket.txt. Expected: non-empty TXT content. " +
                        $"Found: null/empty. Context: order {raw.ExternalOrderId}. " +
                        $"State: cannot parse without TXT content.");
            throw new InvalidOperationException($"Pixfizz TXT content is empty for order {raw.ExternalOrderId}");
        }

        var folderPath = raw.Metadata?.GetValueOrDefault("folder_path") ?? "";
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            AlertCollector.Error(AlertCategory.DataQuality,
                "Pixfizz order missing folder_path in metadata",
                orderId: raw.ExternalOrderId,
                detail: $"Attempted: read folder_path from RawOrder metadata. Expected: valid folder path. " +
                        $"Found: null/empty. Context: order {raw.ExternalOrderId}. " +
                        $"State: cannot calculate file paths without folder_path.");
            throw new InvalidOperationException($"Pixfizz order {raw.ExternalOrderId} missing folder_path");
        }

        var jobId = raw.Metadata?.GetValueOrDefault("job_id");

        // ── Parse TXT (source of truth) ──
        var txtResult = PixfizzTxtParser.ParseContent(txtContent);
        if (txtResult == null)
        {
            AlertCollector.Error(AlertCategory.Parsing,
                $"Pixfizz TXT parse returned null for order {raw.ExternalOrderId}",
                orderId: raw.ExternalOrderId,
                detail: $"Attempted: PixfizzTxtParser.ParseContent(). Expected: valid PixfizzTxtResult. " +
                        $"Found: null. Context: order {raw.ExternalOrderId}, folder '{folderPath}'. " +
                        $"State: order cannot be ingested.");
            throw new InvalidOperationException($"TXT parse returned null for Pixfizz order {raw.ExternalOrderId}");
        }

        // ── Convert items ──
        var items = TxtItemConverter.ToUnifiedItems(txtResult);

        // ── Calculate expected file paths and verify ──
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (string.IsNullOrWhiteSpace(item.ImageFilename))
            {
                AlertCollector.Error(AlertCategory.DataQuality,
                    $"Pixfizz item [{i}] has no ImageFilename",
                    orderId: raw.ExternalOrderId,
                    detail: $"Attempted: calculate file path for item [{i}]. Expected: ImageFilename from TXT. " +
                            $"Found: null/empty. Context: order {raw.ExternalOrderId}, size '{item.SizeLabel}'. " +
                            $"State: item will have no file path.");
                continue;
            }

            var expectedPath = CalculateExpectedPath(folderPath, item.FormatString ?? item.SizeLabel ?? "", item.Quantity, item.ImageFilename);

            items[i] = item with { ImageFilepath = expectedPath };

            var verifyError = OrderHelpers.VerifyFile(expectedPath);
            if (verifyError != null)
            {
                AlertCollector.Error(AlertCategory.DataQuality,
                    $"Pixfizz file not at expected path: {item.ImageFilename}",
                    orderId: raw.ExternalOrderId,
                    detail: $"Attempted: verify file at '{expectedPath}'. " +
                            $"Expected: valid JPEG at calculated path for size '{item.SizeLabel}'. " +
                            $"Found: {verifyError}. " +
                            $"Context: order {raw.ExternalOrderId}, qty {item.Quantity}. " +
                            $"State: file missing or invalid — operator must fix.");
            }
        }

        // ── Build order ──
        var orderId = txtResult.OrderId;
        if (string.IsNullOrWhiteSpace(orderId))
            orderId = raw.ExternalOrderId;
        if (string.IsNullOrWhiteSpace(orderId))
        {
            AlertCollector.Error(AlertCategory.DataQuality,
                "Pixfizz order has no order ID from TXT or metadata",
                orderId: "(unknown)",
                detail: $"Attempted: get order ID from TXT OrderId and RawOrder.ExternalOrderId. " +
                        $"Expected: non-empty order ID. Found: both empty. " +
                        $"Context: folder '{folderPath}'. State: cannot ingest without order ID.");
            throw new InvalidOperationException("Pixfizz order has no order ID");
        }

        DateTime? orderedAt = null;
        if (!string.IsNullOrWhiteSpace(txtResult.ReceivedAt) &&
            DateTime.TryParse(txtResult.ReceivedAt, out var parsedDate))
            orderedAt = parsedDate;

        var order = new UnifiedOrder
        {
            ExternalOrderId = orderId,
            ExternalSource = "pixfizz",
            CustomerFirstName = txtResult.FirstName ?? "",
            CustomerLastName = txtResult.LastName ?? "",
            CustomerEmail = txtResult.Email ?? "",
            CustomerPhone = txtResult.Phone ?? "",
            OrderTotal = txtResult.OrderTotal,
            Location = txtResult.Location ?? "",
            PaymentMethod = txtResult.Payment ?? "",
            Notes = txtResult.Notes ?? "",
            OrderedAt = orderedAt,
            Paid = true,
            FolderPath = folderPath,
            PixfizzJobId = jobId,
            DownloadStatus = IngestConstants.StatusReady,
            Items = items
        };

        // ── Validate ──
        ValidateOrder(order);

        return order;
    }

    /// <summary>
    /// Calculate the expected file path based on the prints/ folder structure.
    /// Structure: {folderPath}/prints/{formatString} format/{qty} prints/{imageFilename}
    /// </summary>
    public static string CalculateExpectedPath(string folderPath, string formatString, int quantity, string imageFilename)
    {
        return Path.Combine(folderPath, "prints",
            $"{formatString} format",
            $"{quantity} prints",
            imageFilename);
    }

    private static void ValidateOrder(UnifiedOrder order)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(order.ExternalOrderId))
            errors.Add("ExternalOrderId is empty");
        if (order.Items.Count == 0)
            errors.Add("zero items");
        if (string.IsNullOrWhiteSpace(order.CustomerFirstName) && string.IsNullOrWhiteSpace(order.CustomerLastName))
            errors.Add("no customer name");

        foreach (var item in order.Items)
        {
            if (string.IsNullOrWhiteSpace(item.SizeLabel))
                errors.Add($"item '{item.ImageFilename}' has no SizeLabel");
            if (item.Quantity <= 0)
                errors.Add($"item '{item.ImageFilename}' has Quantity={item.Quantity}");
            if (string.IsNullOrWhiteSpace(item.ImageFilename))
                errors.Add("item has no ImageFilename");
            if (string.IsNullOrWhiteSpace(item.ImageFilepath))
                errors.Add($"item '{item.ImageFilename}' has no ImageFilepath");
        }

        if (errors.Count > 0)
        {
            var errorList = string.Join("; ", errors);
            AlertCollector.Error(AlertCategory.DataQuality,
                $"Pixfizz order {order.ExternalOrderId} failed validation",
                orderId: order.ExternalOrderId,
                detail: $"Attempted: validate order before return. Expected: all required fields populated. " +
                        $"Found: {errorList}. Context: {order.Items.Count} items, folder '{order.FolderPath}'. " +
                        $"State: order returned but has validation errors.");
        }
    }
}
