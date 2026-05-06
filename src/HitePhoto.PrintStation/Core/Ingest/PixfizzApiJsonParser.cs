using System.IO;
using HitePhoto.Shared.Parsers;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Parses (OHD API job records + order-folder JSON manifest) into a UnifiedOrder.
/// Replaces the legacy PixfizzOrderParser/TxtItemConverter path that read the
/// /Darkroom TXT — the API + JSON together carry everything the TXT did.
///
/// Contract:
/// - One Pixfizz order = many OHD jobs grouped by order_number.
/// - When the order-folder JSON (<orderNumber>.json) is present it gives per-image
///   filename/size/quantity → full items.
/// - When it isn't (unpaid OR a category Pixfizz hasn't yet emitted the template
///   for, e.g. Fine Art Prints), build a stub from API records only; mark items
///   IsNoritsu=false so IngestOrderWriter's file-existence validation doesn't
///   reject them. The ingest service upgrades the stub on a future poll once
///   the manifest appears.
/// </summary>
public class PixfizzApiJsonParser
{
    public UnifiedOrder Parse(
        IReadOnlyList<OhdJobRecord> apiJobs,
        PixfizzOrderManifest? manifest,
        string folderPath)
    {
        if (apiJobs.Count == 0)
            throw new InvalidOperationException("PixfizzApiJsonParser.Parse called with empty apiJobs");

        var orderNumber = apiJobs[0].OrderNumber;
        if (apiJobs.Any(j => j.OrderNumber != orderNumber))
            throw new InvalidOperationException(
                $"PixfizzApiJsonParser.Parse called with mixed order_numbers " +
                $"(first='{orderNumber}', found {apiJobs.Select(j => j.OrderNumber).Distinct().Count()} distinct)");

        var head = apiJobs[0];
        bool isPaid = head.OrderStatus.Equals(IngestConstants.OhdOrderStatusConfirmed, StringComparison.OrdinalIgnoreCase);

        var (firstName, lastName) = SplitCustomerName(head.CustomerName);

        var items = manifest != null
            ? BuildFullItems(apiJobs, manifest, folderPath)
            : BuildStubItems(apiJobs);

        return new UnifiedOrder
        {
            ExternalOrderId = orderNumber,
            ExternalSource = "pixfizz",
            OrderedAt = head.CreatedAt,
            CustomerFirstName = firstName,
            CustomerLastName = lastName,
            CustomerEmail = head.CustomerEmail,
            Notes = head.OrderNotes,
            FolderPath = folderPath,
            Paid = isPaid,
            IsRush = head.IsRush,
            PixfizzJobId = head.JobId,             // first job_id; one-to-many but kept for reference
            PixfizzProductCode = head.ProductCode, // first job's product_code; non-print mixed orders may differ per job
            DownloadStatus = ResolveDownloadStatus(isPaid, head.Category, manifest != null),
            Items = items,
        };
    }

    // ── Full: real items from manifest, joined to API for product/options ─

    private List<UnifiedOrderItem> BuildFullItems(
        IReadOnlyList<OhdJobRecord> apiJobs,
        PixfizzOrderManifest manifest,
        string folderPath)
    {
        var jobsByJobId = apiJobs.ToDictionary(j => j.JobId);
        var items = new List<UnifiedOrderItem>();

        foreach (var manifestJob in manifest.Jobs)
        {
            if (!jobsByJobId.TryGetValue(manifestJob.JobId, out var apiJob))
            {
                AlertCollector.Error(AlertCategory.DataQuality,
                    $"Pixfizz manifest references unknown job_id {manifestJob.JobId} for order {manifest.OrderNumber}",
                    orderId: manifest.OrderNumber,
                    detail: $"Attempted: match manifest job to API record. Expected: API job with id={manifestJob.JobId}. " +
                            $"Found: not in API jobs list. Context: order {manifest.OrderNumber}, " +
                            $"manifest has {manifest.Jobs.Count} jobs, API has {apiJobs.Count}. " +
                            $"State: skipping this manifest job.");
                continue;
            }

            bool isNoritsu = apiJob.Process.Equals(IngestConstants.ProcessNoritsu, StringComparison.OrdinalIgnoreCase);

            var options = apiJob.Options
                .Select(kv => new OrderItemOption(kv.Key, kv.Value))
                .ToList();

            foreach (var img in manifestJob.Images)
            {
                var sizeLabel = !string.IsNullOrWhiteSpace(img.Size) ? img.Size : apiJob.ProductName;
                var format = PixfizzPathHelpers.ComputeFormat(apiJob, img.Size);
                var filename = Path.GetFileName(img.Filename);
                var imagePath = PixfizzPathHelpers.ComputeImagePath(folderPath, format, img.Quantity, filename);

                items.Add(new UnifiedOrderItem
                {
                    ExternalLineId = apiJob.JobId,
                    SizeLabel = sizeLabel,
                    FormatString = format,
                    Quantity = img.Quantity,
                    ImageFilename = filename,
                    ImageFilepath = imagePath,
                    OriginalImageFilepath = imagePath,
                    IsNoritsu = isNoritsu,
                    IsLocalProduction = true,
                    Options = options,
                });
            }
        }

        return items;
    }

    // ── Stub: no manifest yet, IsNoritsu=false bypasses file validation.
    //         Used for unpaid orders AND paid-but-Pixfizz-hasn't-emitted-JSON cases. ──

    private List<UnifiedOrderItem> BuildStubItems(IReadOnlyList<OhdJobRecord> apiJobs)
    {
        var items = new List<UnifiedOrderItem>();
        foreach (var apiJob in apiJobs)
        {
            var format = PixfizzPathHelpers.ComputeFormat(apiJob, manifestImageSize: null);
            var options = apiJob.Options
                .Select(kv => new OrderItemOption(kv.Key, kv.Value))
                .ToList();

            items.Add(new UnifiedOrderItem
            {
                ExternalLineId = apiJob.JobId,
                SizeLabel = apiJob.ProductName,
                FormatString = format,
                Quantity = apiJob.Quantity,
                ImageFilename = "",
                ImageFilepath = "",
                OriginalImageFilepath = "",
                IsNoritsu = false,        // critical: bypasses IngestOrderWriter's file-existence check
                IsLocalProduction = true,
                Options = options,
            });
        }
        return items;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Maps (paid, category, manifest-present) into a specific stub reason so the
    /// operator UI can show *why* a stub has no files.
    /// </summary>
    private static string ResolveDownloadStatus(bool isPaid, string category, bool hasManifest)
    {
        if (hasManifest) return IngestConstants.StatusReady;
        if (!isPaid) return IngestConstants.StatusUnpaid;
        if (string.Equals(category, IngestConstants.CategoryFilmProcessing, StringComparison.OrdinalIgnoreCase))
            return IngestConstants.StatusNoArtworkExpected;
        return IngestConstants.StatusAwaitingFiles;
    }

    private static (string First, string Last) SplitCustomerName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return ("", "");
        var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return (parts[0], "");
        // Last token = last name, everything else = first name (handles "Mary Jane Smith" → "Mary Jane", "Smith")
        return (string.Join(' ', parts[..^1]), parts[^1]);
    }

}

// ── DTOs ──────────────────────────────────────────────────────────────────

/// <summary>
/// One job from OHD /jobs/pending — already extracted from the raw API JSON.
/// The poller does the JSON-to-record mapping; this parser doesn't touch HTTP.
/// </summary>
public record OhdJobRecord(
    string JobId,
    string OrderNumber,
    string OrderIdHash,
    string Process,
    string Category,
    string ProductCode,
    string ProductName,
    int Quantity,
    string OrderStatus,
    string CustomerName,
    string CustomerEmail,
    DateTime CreatedAt,
    DateTime? DueDate,
    string OrderNotes,
    bool IsRush,
    IReadOnlyDictionary<string, string> Options);

/// <summary>
/// The <orderNumber>.json manifest sitting inside each /Artwork/<orderNumber>_<hash>/ folder.
/// </summary>
public record PixfizzOrderManifest(
    string OrderId,
    string OrderNumber,
    List<PixfizzManifestJob> Jobs);

public record PixfizzManifestJob(
    string JobId,
    List<PixfizzManifestImage> Images);

public record PixfizzManifestImage(
    string Filename,
    string Size,
    int Quantity);
