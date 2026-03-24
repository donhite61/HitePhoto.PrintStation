using System.Text.Json;
using HitePhoto.Shared.Parsers;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Parses Pixfizz OHD API JSON for job discovery only.
/// The API tells us WHAT to download (job_id, order_number).
/// The TXT file tells us everything else (customer, sizes, quantities, images).
/// No customer data from the API is trusted.
/// </summary>
public class PixfizzOrderParser
{
    /// <summary>
    /// Extract only what we need from the API: order number, job ID, and enough
    /// to create a shell order for the downloader to fill in from TXT.
    /// </summary>
    public UnifiedOrder Parse(RawOrder raw)
    {
        using var doc = JsonDocument.Parse(raw.RawData);
        var el = doc.RootElement;

        var orderNumber = raw.ExternalOrderId;

        // Try to get order_number from nested or flat format
        if (el.TryGetProperty("order", out var orderEl) && orderEl.ValueKind == JsonValueKind.Object)
        {
            var num = JsonUtils.GetStr(orderEl, "order_number");
            if (!string.IsNullOrEmpty(num)) orderNumber = num;
        }
        else
        {
            var num = JsonUtils.GetStr(el, "order_number");
            if (!string.IsNullOrEmpty(num)) orderNumber = num;
        }

        var pixfizzJobId = raw.Metadata?.GetValueOrDefault("job_id");

        // Shell order — TXT will fill in everything else
        return new UnifiedOrder
        {
            ExternalOrderId = orderNumber,
            ExternalSource = "pixfizz",
            Paid = true,
            PixfizzJobId = pixfizzJobId
        };
    }

    /// <summary>
    /// Fills the shell order with authoritative data from the parsed TXT.
    /// TXT is the source of truth for all order content.
    /// </summary>
    public static UnifiedOrder FillFromTxt(UnifiedOrder shell, PixfizzTxtResult txt)
    {
        DateTime? orderedAt = null;
        if (!string.IsNullOrWhiteSpace(txt.ReceivedAt) &&
            DateTime.TryParse(txt.ReceivedAt, out var parsed))
            orderedAt = parsed;

        return shell with
        {
            CustomerFirstName = txt.FirstName ?? "",
            CustomerLastName = txt.LastName ?? "",
            CustomerEmail = txt.Email ?? "",
            CustomerPhone = txt.Phone ?? "",
            OrderTotal = txt.OrderTotal,
            Location = txt.Location ?? "",
            PaymentMethod = txt.Payment ?? "",
            OrderedAt = orderedAt
        };
    }
}
