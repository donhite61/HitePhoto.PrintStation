using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Polls OHD /jobs/pending and groups jobs by order_number. Each group is
/// ready to feed into PixfizzApiJsonParser.
///
/// Replaces the legacy OhdApiSource which produced one RawOrder per job and
/// included a hard "Cut Prints" filter — we now want every category so
/// non-print orders (mugs, acrylic, canvas) and unpaid stubs both flow
/// through the same pipeline.
/// </summary>
public class OhdJobsPoller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly AppSettings _settings;

    public OhdJobsPoller(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Returns one group per Pixfizz order_number, with all of that order's
    /// pending jobs attached. Already filtered by this store's location.
    /// </summary>
    public async Task<IReadOnlyList<OhdOrderGroup>> PollAsync(CancellationToken ct)
    {
        var url = $"{_settings.PixfizzApiUrl.TrimEnd('/')}/ohd-api/jobs/pending";
        var json = await SendWithRetryAsync(url, ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement jobsArray;
        if (root.ValueKind == JsonValueKind.Array)
            jobsArray = root;
        else if (root.TryGetProperty("jobs", out var jobsEl) && jobsEl.ValueKind == JsonValueKind.Array)
            jobsArray = jobsEl;
        else
        {
            AppLog.Info("OHD API response had no jobs array");
            return [];
        }

        var allJobs = new List<OhdJobRecord>();

        foreach (var jobEl in jobsArray.EnumerateArray())
        {
            if (!PassesLocationFilter(jobEl)) continue;

            var record = MapJob(jobEl);
            if (record == null) continue;

            allJobs.Add(record);
        }

        var grouped = allJobs
            .GroupBy(j => j.OrderNumber)
            .Select(g => new OhdOrderGroup(g.Key, g.ToList()))
            .ToList();

        AppLog.Info($"OHD poll: {allJobs.Count} jobs across {grouped.Count} orders for this store");
        return grouped;
    }

    private static OhdJobRecord? MapJob(JsonElement jobEl)
    {
        var jobId = JsonUtils.GetStr(jobEl, "job_id");
        if (string.IsNullOrEmpty(jobId)) jobId = JsonUtils.GetStr(jobEl, "id");
        if (string.IsNullOrEmpty(jobId)) return null;

        var orderNumber = JsonUtils.GetStr(jobEl, "order_number");
        if (string.IsNullOrEmpty(orderNumber))
        {
            // Fallback: nested order object (older API shape)
            if (jobEl.TryGetProperty("order", out var orderEl) && orderEl.ValueKind == JsonValueKind.Object)
                orderNumber = JsonUtils.GetStr(orderEl, "order_number");
        }
        if (string.IsNullOrEmpty(orderNumber)) return null;

        var orderId = JsonUtils.GetStr(jobEl, "order_id");

        return new OhdJobRecord(
            JobId: jobId,
            OrderNumber: orderNumber,
            OrderIdHash: orderId,
            Process: JsonUtils.GetStr(jobEl, "process"),
            Category: JsonUtils.GetStr(jobEl, "category"),
            ProductCode: JsonUtils.GetStr(jobEl, "product_code"),
            ProductName: JsonUtils.GetStr(jobEl, "product_name"),
            Quantity: JsonUtils.GetInt(jobEl, "quantity", defaultValue: 1),
            OrderStatus: JsonUtils.GetStr(jobEl, "order_status"),
            CustomerName: JsonUtils.GetStr(jobEl, "customer_name"),
            CustomerEmail: JsonUtils.GetStr(jobEl, "customer_email"),
            CreatedAt: JsonUtils.GetDate(jobEl, "created_at") ?? DateTime.Now,
            DueDate: JsonUtils.GetDate(jobEl, "due_date"),
            OrderNotes: JsonUtils.GetStr(jobEl, "order_notes"),
            IsRush: JsonUtils.GetBool(jobEl, "is_rush"),
            Options: ExtractOptions(jobEl));
    }

    private static IReadOnlyDictionary<string, string> ExtractOptions(JsonElement jobEl)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!jobEl.TryGetProperty("options", out var optsEl) || optsEl.ValueKind != JsonValueKind.Array)
            return dict;

        foreach (var opt in optsEl.EnumerateArray())
        {
            if (opt.ValueKind != JsonValueKind.Object) continue;
            var key = JsonUtils.GetStr(opt, "key");
            var value = JsonUtils.GetStr(opt, "value");
            if (!string.IsNullOrEmpty(key))
                dict[key] = value;
        }
        return dict;
    }

    private bool PassesLocationFilter(JsonElement jobEl)
    {
        if (string.IsNullOrEmpty(_settings.PixfizzLocationId))
            return true;

        if (!jobEl.TryGetProperty("locations", out var locationsEl) || locationsEl.ValueKind != JsonValueKind.Array)
            return true;

        foreach (var loc in locationsEl.EnumerateArray())
        {
            if (loc.ValueKind == JsonValueKind.String &&
                string.Equals(loc.GetString(), _settings.PixfizzLocationId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ── HTTP with retry — adapted from legacy OhdApiSource ───────────────

    private async Task<string> SendWithRetryAsync(string url, CancellationToken ct, int maxRetries = 3)
    {
        var delay = TimeSpan.FromSeconds(2);

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.PixfizzApiKey);
                request.Headers.Add("X-API-Key", _settings.PixfizzApiKey);

                if (!string.IsNullOrEmpty(_settings.PixfizzOrganizationId))
                    request.Headers.Add("X-Organization-ID", _settings.PixfizzOrganizationId);
                if (!string.IsNullOrEmpty(_settings.PixfizzLocationId))
                    request.Headers.Add("X-Location-ID", _settings.PixfizzLocationId);

                using var response = await Http.SendAsync(request, ct);
                var json = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode) return json;

                if ((int)response.StatusCode < 500)
                    throw new Exception($"OHD API error {response.StatusCode}: {json}");

                AppLog.Info($"OHD API returned {response.StatusCode} — retrying ({attempt}/{maxRetries})");
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                AppLog.Info($"OHD API request failed — retrying ({attempt}/{maxRetries})");
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException) when (attempt < maxRetries)
            {
                AppLog.Info($"OHD API timed out — retrying ({attempt}/{maxRetries})");
            }

            await Task.Delay(delay, ct);
            delay *= 2;
        }

        throw new Exception($"OHD API failed after {maxRetries} attempts");
    }
}

/// <summary>
/// All pending OHD jobs that share the same order_number, ready to feed
/// into PixfizzApiJsonParser as one parser call.
/// </summary>
public record OhdOrderGroup(string OrderNumber, IReadOnlyList<OhdJobRecord> Jobs);
