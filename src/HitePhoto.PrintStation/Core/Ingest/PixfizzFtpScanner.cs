using System.Text.RegularExpressions;
using FluentFTP;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Scans the Pixfizz FTP /Artwork folder for waiting orders.
/// Pixfizz encodes the order identity in the folder name itself
/// (e.g. <c>HITEPHOTO-MX5V8M_69f90c98a2473e8e</c>), so the scanner
/// just lists /Artwork and parses each directory name — no JSON
/// manifest, no nested listing, no OHD API call.
/// </summary>
public class PixfizzFtpScanner
{
    // {orderNumber}_{16-hex orderId hash}
    private static readonly Regex FolderNameRegex =
        new(@"^(HITEPHOTO-[^_]+)_([a-f0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AppSettings _settings;

    public PixfizzFtpScanner(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<List<PixfizzFtpOrder>> ScanAsync(CancellationToken ct)
    {
        var results = new List<PixfizzFtpOrder>();

        using var client = PixfizzFtpDownloader.CreateClient(_settings);
        await client.Connect(ct);

        var artworkRoot = _settings.PixfizzFtpArtworkFolder.TrimEnd('/');
        var listing = await client.GetListing(artworkRoot, ct);

        foreach (var dir in listing.Where(f => f.Type == FtpObjectType.Directory))
        {
            var match = FolderNameRegex.Match(dir.Name);
            if (!match.Success)
            {
                AppLog.Info($"PixfizzFtpScanner: skipping unrecognized folder name '{dir.Name}'");
                continue;
            }

            results.Add(new PixfizzFtpOrder
            {
                OrderNumber = match.Groups[1].Value,
                OrderId = match.Groups[2].Value
            });
        }

        return results;
    }
}

public class PixfizzFtpOrder
{
    public required string OrderNumber { get; init; }
    public required string OrderId { get; init; }
}
