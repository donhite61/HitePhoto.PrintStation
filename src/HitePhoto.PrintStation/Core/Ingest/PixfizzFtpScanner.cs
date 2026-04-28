using System.IO;
using System.Text.Json;
using FluentFTP;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Scans the Pixfizz FTP /Artwork folder for order JSON manifests.
/// Returns discovered orders that can be downloaded without the API.
/// </summary>
public class PixfizzFtpScanner
{
    private readonly AppSettings _settings;

    public PixfizzFtpScanner(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Scan /Artwork on FTP. Returns a list of orders found, each with
    /// orderNumber, orderId, and the list of jobs from the JSON manifest.
    /// </summary>
    public async Task<List<PixfizzFtpOrder>> ScanAsync(CancellationToken ct)
    {
        var results = new List<PixfizzFtpOrder>();

        using var client = PixfizzFtpDownloader.CreateClient(_settings);
        await client.Connect(ct);

        var artworkRoot = _settings.PixfizzFtpArtworkFolder.TrimEnd('/');
        var listing = await client.GetListing(artworkRoot, ct);

        foreach (var dir in listing.Where(f => f.Type == FtpObjectType.Directory))
        {
            try
            {
                // Each order folder contains a JSON manifest: {orderNumber}.json
                var folderContents = await client.GetListing($"{artworkRoot}/{dir.Name}", ct);
                var jsonFile = folderContents.FirstOrDefault(f =>
                    f.Type == FtpObjectType.File &&
                    f.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

                if (jsonFile == null) continue;

                // Download and parse the JSON
                using var ms = new MemoryStream();
                if (!await client.DownloadStream(ms, $"{artworkRoot}/{dir.Name}/{jsonFile.Name}", token: ct))
                    continue;

                ms.Position = 0;
                var manifest = await JsonSerializer.DeserializeAsync<PixfizzManifest>(ms,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);

                if (manifest == null || string.IsNullOrEmpty(manifest.OrderNumber))
                    continue;

                results.Add(new PixfizzFtpOrder
                {
                    OrderNumber = manifest.OrderNumber,
                    OrderId = manifest.OrderId ?? "",
                    FolderName = dir.Name,
                    Jobs = manifest.Jobs ?? []
                });
            }
            catch (Exception ex)
            {
                AppLog.Info($"PixfizzFtpScanner: failed to read manifest from {dir.Name}: {ex.Message}");
            }
        }

        return results;
    }

}

public class PixfizzManifest
{
    public string? OrderId { get; set; }
    public string? OrderNumber { get; set; }
    public List<PixfizzManifestJob>? Jobs { get; set; }
}

public class PixfizzManifestJob
{
    public string? JobId { get; set; }
    public List<PixfizzManifestImage>? Images { get; set; }
}

public class PixfizzManifestImage
{
    public string? Filename { get; set; }
    public string? Size { get; set; }
    public int Quantity { get; set; }
}

public class PixfizzFtpOrder
{
    public required string OrderNumber { get; init; }
    public required string OrderId { get; init; }
    public required string FolderName { get; init; }
    public List<PixfizzManifestJob> Jobs { get; init; } = [];
}
