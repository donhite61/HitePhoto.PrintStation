using System.Net.Http;
using System.Net.Http.Headers;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Calls POST /ohd-api/jobs/{jobId}/received after download completes.
/// CRITICAL: Only call AFTER FTP download succeeds AND order has been verified for 24 hours.
/// If we mark received but download failed, the job vanishes from pending and we lose it.
/// </summary>
public class OhdReceivedPusher
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly AppSettings _settings;

    public OhdReceivedPusher(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Marks a job as received on the OHD API.
    /// jobId is the Pixfizz job_id (not the order number).
    /// </summary>
    public async Task MarkReceivedAsync(string jobId, CancellationToken ct)
    {
        var url = $"{_settings.PixfizzApiUrl.TrimEnd('/')}/ohd-api/jobs/{jobId}/received";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(
            $"{{\"timestamp\":\"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\"}}",
            System.Text.Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.PixfizzApiKey);
        request.Headers.Add("X-API-Key", _settings.PixfizzApiKey);

        if (!string.IsNullOrEmpty(_settings.PixfizzOrganizationId))
            request.Headers.Add("X-Organization-ID", _settings.PixfizzOrganizationId);
        if (!string.IsNullOrEmpty(_settings.PixfizzLocationId))
            request.Headers.Add("X-Location-ID", _settings.PixfizzLocationId);

        using var response = await Http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            // Tolerate "already received" — idempotent
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest &&
                body.Contains("already", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Info($"Job {jobId} already marked as received");
                return;
            }

            throw new Exception($"OHD /received failed {response.StatusCode}: {body}");
        }

        AppLog.Info($"Marked job {jobId} as received on OHD API");
    }
}
