using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HitePhoto.PrintStation.Core.Processing;

/// <summary>
/// Slim Pixfizz API client for PrintStation — only mark-completed.
/// PrintStation doesn't poll or download orders (IngestService does that).
/// </summary>
public class PixfizzNotifier
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _organizationId;
    private readonly string _locationId;

    public PixfizzNotifier(string apiKey, string baseUrl = "",
        string organizationId = "", string locationId = "")
    {
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? "https://nazkcvruighrhpgcarxg.supabase.co/functions/v1"
            : baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _organizationId = organizationId;
        _locationId = locationId;
    }

    /// <summary>
    /// POST /ohd-api/jobs/{jobId}/completed — marks order done in Pixfizz.
    /// Pixfizz then sends the customer notification email.
    /// </summary>
    public async Task<bool> MarkCompletedAsync(string jobId)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{_baseUrl}/ohd-api/jobs/{jobId}/completed");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Headers.Add("X-API-Key", _apiKey);
            if (!string.IsNullOrEmpty(_organizationId))
                request.Headers.Add("X-Organization-ID", _organizationId);
            if (!string.IsNullOrEmpty(_locationId))
                request.Headers.Add("X-Location-ID", _locationId);

            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                // Tolerate "already completed" responses
                if (body.Contains("already", StringComparison.OrdinalIgnoreCase))
                    return true;

                AlertCollector.Error(AlertCategory.Network,
                    "Pixfizz mark-completed failed",
                    detail: $"Attempted: POST /ohd-api/jobs/{jobId}/completed. " +
                            $"Expected: 200 OK. Found: {response.StatusCode}. Body: {body}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Network,
                "Pixfizz mark-completed exception",
                detail: $"Attempted: POST completed for job {jobId}. Found: exception.",
                ex: ex);
            return false;
        }
    }
}
