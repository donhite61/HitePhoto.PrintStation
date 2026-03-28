using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Polls the OHD API (/ohd-api/jobs/pending) for new Pixfizz print jobs.
/// Client-side location UUID filter enforces store assignment.
/// </summary>
public class OhdApiSource : IOrderSource
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly AppSettings _settings;

    public string SourceName => "pixfizz";

    public OhdApiSource(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<IReadOnlyList<RawOrder>> PollAsync(CancellationToken ct)
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
            return [];

        var results = new List<RawOrder>();

        foreach (var jobEl in jobsArray.EnumerateArray())
        {
            // Only accept cut-print jobs
            if (jobEl.TryGetProperty("category", out var cat) &&
                cat.ValueKind == JsonValueKind.String &&
                !cat.GetString()!.Contains("Cut Prints", StringComparison.OrdinalIgnoreCase))
                continue;

            var jobId = JsonUtils.GetStr(jobEl, "job_id");
            if (string.IsNullOrEmpty(jobId))
            {
                jobId = JsonUtils.GetStr(jobEl, "id");
                if (string.IsNullOrEmpty(jobId)) continue;
            }

            var orderId = JsonUtils.GetStr(jobEl, "order_number");
            if (string.IsNullOrEmpty(orderId))
            {
                if (jobEl.TryGetProperty("order", out var orderEl) && orderEl.ValueKind == JsonValueKind.Object)
                {
                    var orderNumber = JsonUtils.GetStr(orderEl, "order_number");
                    if (!string.IsNullOrEmpty(orderNumber))
                        orderId = orderNumber;
                }
                if (string.IsNullOrEmpty(orderId))
                    orderId = jobId;
            }

            if (!PassesLocationFilter(jobEl))
            {
                AppLog.Info($"Skipping job {jobId} (order {orderId}) — location doesn't match '{_settings.PixfizzLocationId}'");
                continue;
            }

            results.Add(new RawOrder(
                ExternalOrderId: orderId,
                SourceName: SourceName,
                RawData: jobEl.GetRawText(),
                Metadata: new Dictionary<string, string> { ["job_id"] = jobId }));
        }

        AppLog.Info($"OHD API returned {results.Count} pending jobs for this store");
        return results;
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

                if (response.IsSuccessStatusCode)
                    return json;

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
