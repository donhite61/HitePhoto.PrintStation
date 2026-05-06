using System.IO;
using System.Text.Json;
using FluentFTP.Exceptions;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Fetches and deserializes the <orderNumber>.json manifest sitting inside each
/// /Artwork/<orderNumber>_<orderIdHash>/ folder on the Pixfizz FTP.
/// Returns null when the file isn't there (unpaid orders, or transient FTP states).
/// </summary>
public class PixfizzManifestReader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppSettings _settings;

    public PixfizzManifestReader(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<PixfizzOrderManifest?> FetchAsync(
        string orderNumber, string orderIdHash, CancellationToken ct)
    {
        using var client = PixfizzFtpDownloader.CreateClient(_settings);
        await client.Connect(ct);

        var artworkRoot = _settings.PixfizzFtpArtworkFolder.TrimEnd('/');
        var remotePath = $"{artworkRoot}/{orderNumber}_{orderIdHash}/{orderNumber}.json";

        using var ms = new MemoryStream();
        try
        {
            if (!await client.DownloadStream(ms, remotePath, token: ct))
                return null;
        }
        catch (FtpMissingObjectException)
        {
            // No JSON manifest in this order's folder — expected for orders
            // whose Pixfizz account doesn't have the JSON Fulfillment Template
            // enabled (e.g. WB as of 2026-05-05). Caller alerts as appropriate.
            return null;
        }

        ms.Position = 0;
        try
        {
            return await JsonSerializer.DeserializeAsync<PixfizzOrderManifest>(ms, JsonOpts, ct);
        }
        catch (JsonException ex)
        {
            AlertCollector.Error(AlertCategory.Parsing,
                $"Pixfizz manifest deserialization failed for {orderNumber}",
                orderId: orderNumber,
                detail: $"Attempted: deserialize {remotePath} as PixfizzOrderManifest. " +
                        $"Expected: valid JSON. Found: {ex.Message}. " +
                        $"State: cannot ingest order without manifest.",
                ex: ex);
            return null;
        }
    }
}
