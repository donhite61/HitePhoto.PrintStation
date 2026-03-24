using System.Text.Json;
using HitePhoto.Shared.Parsers;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Parses Pixfizz OrderHub API JSON into UnifiedOrder format.
///
/// Two-authority design:
///   - Darkroom TXT is authoritative for sizes, quantities, and image lists
///   - API provides customer info, store ID, rush flag as supplement
///
/// Size is NEVER inferred from product_code or product_name.
/// </summary>
public class PixfizzOrderParser
{
    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    public UnifiedOrder Parse(RawOrder raw)
    {
        using var doc = JsonDocument.Parse(raw.RawData);
        var el = doc.RootElement;

        string customerFirstName = "", customerLastName = "", customerEmail = "", customerPhone = "";
        string orderNumber = raw.ExternalOrderId;
        decimal totalAmount = 0;
        string storeIdentifier = "";
        string orderNotes = "";
        DateTime? orderedAt = null;
        bool isRush = false;

        JsonElement orderEl = default;
        bool hasOrder = el.TryGetProperty("order", out orderEl) && orderEl.ValueKind == JsonValueKind.Object;

        if (hasOrder)
        {
            var fullName = GetStr(orderEl, "customer_name");
            SplitFullName(fullName, out customerFirstName, out customerLastName);
            customerEmail = GetStr(orderEl, "customer_email");
            customerPhone = GetStr(orderEl, "customer_phone");
            storeIdentifier = GetStr(orderEl, "store_identifier");
            orderNotes = GetStr(orderEl, "notes");

            var num = GetStr(orderEl, "order_number");
            if (!string.IsNullOrEmpty(num)) orderNumber = num;

            if (orderEl.TryGetProperty("total_amount", out var amt) && amt.ValueKind == JsonValueKind.Number)
                totalAmount = amt.GetDecimal();
        }

        // OHD flat format fallbacks
        if (string.IsNullOrEmpty(customerFirstName) && string.IsNullOrEmpty(customerLastName))
        {
            var flatName = GetStr(el, "customer_name");
            SplitFullName(flatName, out customerFirstName, out customerLastName);
        }
        if (string.IsNullOrEmpty(customerEmail))
            customerEmail = GetStr(el, "customer_email");
        if (string.IsNullOrEmpty(customerPhone))
            customerPhone = GetStr(el, "customer_phone");
        if (string.IsNullOrEmpty(orderNotes))
            orderNotes = GetStr(el, "order_notes");
        var flatOrderNumber = GetStr(el, "order_number");
        if (!string.IsNullOrEmpty(flatOrderNumber) && orderNumber == raw.ExternalOrderId)
            orderNumber = flatOrderNumber;

        // Store identifier fallback chain
        if (string.IsNullOrEmpty(storeIdentifier))
            storeIdentifier = GetStr(el, "website");
        if (string.IsNullOrEmpty(storeIdentifier))
            storeIdentifier = GetStr(el, "location_id");
        if (string.IsNullOrEmpty(storeIdentifier))
            storeIdentifier = ExtractFromLocationsArray(el);
        if (string.IsNullOrEmpty(storeIdentifier) && hasOrder)
        {
            storeIdentifier = GetStr(orderEl, "location_id");
            if (string.IsNullOrEmpty(storeIdentifier))
                storeIdentifier = ExtractFromLocationsArray(orderEl);
        }

        // Parse structured fields from job notes
        var jobNotes = GetStr(el, "notes");
        var parsedNotes = ParseStructuredNotes(jobNotes);

        if (string.IsNullOrEmpty(storeIdentifier) && !string.IsNullOrEmpty(parsedNotes.Fulfillment))
            storeIdentifier = parsedNotes.Fulfillment;

        if (string.IsNullOrEmpty(storeIdentifier))
        {
            AlertCollector.Error(AlertCategory.DataQuality,
                "No store identifier found in API response",
                orderId: orderNumber,
                detail: $"Checked: store_identifier, website, location_id, locations[], Fulfillment notes. All empty.");
        }

        // Rush flag
        if (el.TryGetProperty("is_rush", out var rushEl) && rushEl.ValueKind == JsonValueKind.True)
            isRush = true;

        // Timestamp
        var createdAtStr = GetStr(el, "created_at");
        if (string.IsNullOrEmpty(createdAtStr) && orderEl.ValueKind == JsonValueKind.Object)
            createdAtStr = GetStr(orderEl, "created_at");
        if (!string.IsNullOrEmpty(createdAtStr) && DateTime.TryParse(createdAtStr, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsedUtc))
            orderedAt = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(parsedUtc, DateTimeKind.Utc), Eastern);

        // Combined notes (free text only, not structured metadata)
        var productionNotes = GetStr(el, "production_notes");
        var combinedNotes = string.Join(" | ",
            new[] { parsedNotes.FreeText, orderNotes, productionNotes }
            .Where(n => !string.IsNullOrWhiteSpace(n)));

        // Quantity from API (fallback if no TXT)
        int apiQuantity = 1;
        if (el.TryGetProperty("quantity", out var qty) && qty.ValueKind == JsonValueKind.Number)
            apiQuantity = qty.GetInt32();

        // Build items from artwork_files (no size info — TXT fills that in later)
        var items = new List<UnifiedOrderItem>();
        if (el.TryGetProperty("artwork_files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var fileEl in files.EnumerateArray())
            {
                items.Add(new UnifiedOrderItem
                {
                    ExternalLineId = GetStr(fileEl, "id"),
                    Quantity = apiQuantity,
                    ImageFilename = GetStr(fileEl, "file_name")
                });
            }
        }

        if (items.Count == 0)
        {
            items.Add(new UnifiedOrderItem
            {
                ExternalLineId = raw.Metadata?.GetValueOrDefault("job_id"),
                Quantity = apiQuantity,
                ImageFilename = GetStr(el, "file_name")
            });
        }

        var pixfizzJobId = raw.Metadata?.GetValueOrDefault("job_id");

        return new UnifiedOrder
        {
            ExternalOrderId = orderNumber,
            ExternalSource = storeIdentifier,
            OrderedAt = orderedAt,
            CustomerFirstName = customerFirstName,
            CustomerLastName = customerLastName,
            CustomerEmail = customerEmail,
            CustomerPhone = customerPhone,
            OrderTotal = totalAmount,
            Paid = true,
            Notes = combinedNotes,
            Location = storeIdentifier,
            IsRush = isRush,
            PixfizzJobId = pixfizzJobId,
            PixfizzBookId = parsedNotes.BookId,
            PixfizzProductCode = parsedNotes.Product,
            FulfillmentStoreName = parsedNotes.Fulfillment,
            Items = items
        };
    }

    /// <summary>
    /// Merges TXT-parsed data into an API-parsed order. TXT is authoritative.
    /// Items are set by the caller after path rewriting.
    /// </summary>
    public static UnifiedOrder MergeWithTxt(UnifiedOrder apiOrder, PixfizzTxtResult txtResult)
    {
        DateTime? txtOrderedAt = null;
        if (!string.IsNullOrWhiteSpace(txtResult.ReceivedAt) &&
            DateTime.TryParse(txtResult.ReceivedAt, out var parsed))
            txtOrderedAt = parsed;

        return apiOrder with
        {
            CustomerFirstName = !string.IsNullOrWhiteSpace(txtResult.FirstName) ? txtResult.FirstName : apiOrder.CustomerFirstName,
            CustomerLastName = !string.IsNullOrWhiteSpace(txtResult.LastName) ? txtResult.LastName : apiOrder.CustomerLastName,
            CustomerEmail = !string.IsNullOrWhiteSpace(txtResult.Email) ? txtResult.Email : apiOrder.CustomerEmail,
            CustomerPhone = !string.IsNullOrWhiteSpace(txtResult.Phone) ? txtResult.Phone : apiOrder.CustomerPhone,
            OrderTotal = txtResult.OrderTotal > 0 ? txtResult.OrderTotal : apiOrder.OrderTotal,
            Location = !string.IsNullOrWhiteSpace(txtResult.Location) ? txtResult.Location : apiOrder.Location,
            PaymentMethod = !string.IsNullOrWhiteSpace(txtResult.Payment) ? txtResult.Payment : apiOrder.PaymentMethod,
            FulfillmentStoreName = !string.IsNullOrWhiteSpace(txtResult.Location) ? txtResult.Location : apiOrder.FulfillmentStoreName,
            OrderedAt = txtOrderedAt ?? apiOrder.OrderedAt
        };
    }

    // ── Helpers ──

    private static string ExtractFromLocationsArray(JsonElement el)
    {
        if (el.TryGetProperty("locations", out var locs) && locs.ValueKind == JsonValueKind.Array)
        {
            foreach (var loc in locs.EnumerateArray())
            {
                var s = loc.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        return "";
    }

    internal static ParsedNotes ParseStructuredNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return new ParsedNotes();

        string? product = null, productId = null, bookId = null, fulfillment = null;
        var freeTextLines = new List<string>();

        foreach (var rawLine in notes.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (TryExtract(line, "Product:", out var val) && product == null)
                product = val;
            else if (TryExtract(line, "Product ID:", out val))
                productId = val;
            else if (TryExtract(line, "Book ID:", out val))
                bookId = val;
            else if (TryExtract(line, "Fulfillment:", out val))
                fulfillment = val;
            else if (TryExtract(line, "Category source:", out _))
            { }
            else
                freeTextLines.Add(line);
        }

        return new ParsedNotes
        {
            Product = product, ProductId = productId,
            BookId = bookId, Fulfillment = fulfillment,
            FreeText = freeTextLines.Count > 0 ? string.Join(" | ", freeTextLines) : null
        };
    }

    private static bool TryExtract(string line, string prefix, out string? value)
    {
        if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = line[prefix.Length..].Trim();
            return true;
        }
        value = null;
        return false;
    }

    private static string GetStr(JsonElement el, string prop) => JsonUtils.GetStr(el, prop);

    private static void SplitFullName(string fullName, out string first, out string last)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            first = "";
            last = "";
            return;
        }

        var spaceIdx = fullName.IndexOf(' ');
        if (spaceIdx > 0)
        {
            first = fullName[..spaceIdx].Trim();
            last = fullName[(spaceIdx + 1)..].Trim();
        }
        else
        {
            first = fullName.Trim();
            last = "";
        }
    }
}

internal record ParsedNotes
{
    public string? Product { get; init; }
    public string? ProductId { get; init; }
    public string? BookId { get; init; }
    public string? Fulfillment { get; init; }
    public string? FreeText { get; init; }
}
