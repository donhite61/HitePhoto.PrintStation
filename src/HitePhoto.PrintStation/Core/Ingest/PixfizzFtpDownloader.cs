using System.IO;
using FluentFTP;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Downloads Pixfizz artwork and darkroom TXT files via FTP.
/// </summary>
public class PixfizzFtpDownloader
{
    private readonly AppSettings _settings;

    public PixfizzFtpDownloader(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    private AsyncFtpClient CreateClient()
    {
        var client = new AsyncFtpClient(
            _settings.PixfizzFtpServer,
            _settings.PixfizzFtpUsername,
            _settings.PixfizzFtpPassword,
            _settings.PixfizzFtpPort);
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        client.Config.EncryptionMode = FtpEncryptionMode.None;
        client.Config.ConnectTimeout = 15000;
        client.Config.ReadTimeout = 15000;
        client.Config.DataConnectionConnectTimeout = 15000;
        return client;
    }

    /// <summary>
    /// Downloads the darkroom TXT file for a specific job from FTP.
    /// Matches "{orderNumber}_{jobId}.txt" exactly.
    /// Returns TXT content or null if not found.
    /// </summary>
    public async Task<string?> DownloadDarkroomTxtAsync(string orderNumber, CancellationToken ct, string? jobId = null)
    {
        using var client = CreateClient();
        await client.Connect(ct);

        var remotePath = _settings.PixfizzFtpDarkroomFolder.TrimEnd('/');
        var listing = await client.GetListing(remotePath, ct);

        var txtFiles = listing.Where(f =>
            f.Type == FtpObjectType.File &&
            f.Name.StartsWith(orderNumber, StringComparison.OrdinalIgnoreCase) &&
            f.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).ToList();

        FtpListItem? match = null;
        if (!string.IsNullOrEmpty(jobId))
        {
            var expectedName = $"{orderNumber}_{jobId}.txt";
            match = txtFiles.FirstOrDefault(f =>
                f.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                AlertCollector.Error(AlertCategory.Network,
                    $"Darkroom TXT not found for job {jobId}",
                    orderId: orderNumber,
                    detail: $"Expected: '{expectedName}' in FTP {remotePath}. " +
                            $"Available: [{string.Join(", ", txtFiles.Select(f => f.Name))}]");
            }
        }
        else
        {
            if (txtFiles.Count == 1)
                match = txtFiles[0];
            else if (txtFiles.Count > 1)
            {
                AlertCollector.Error(AlertCategory.DataQuality,
                    $"Multiple TXT files for order, no job ID to disambiguate",
                    orderId: orderNumber,
                    detail: $"Found {txtFiles.Count} files: [{string.Join(", ", txtFiles.Select(f => f.Name))}]");
            }
        }

        if (match == null)
            return null;

        using var ms = new MemoryStream();
        if (await client.DownloadStream(ms, $"{remotePath}/{match.Name}", token: ct))
        {
            ms.Position = 0;
            using var reader = new StreamReader(ms);
            return await reader.ReadToEndAsync(ct);
        }

        return null;
    }

    /// <summary>
    /// Downloads all artwork files for an order into the local order folder.
    /// Returns list of downloaded local file paths.
    /// </summary>
    public async Task<List<string>> DownloadArtworkAsync(
        string orderNumber, string orderId, string localOrderFolder, CancellationToken ct)
    {
        using var client = CreateClient();
        await client.Connect(ct);

        var artworkRoot = _settings.PixfizzFtpArtworkFolder.TrimEnd('/');
        var downloaded = new List<string>();

        var listing = await client.GetListing(artworkRoot, ct);
        var orderFolders = listing.Where(f =>
            f.Type == FtpObjectType.Directory &&
            f.Name.StartsWith(orderNumber, StringComparison.OrdinalIgnoreCase));

        foreach (var orderDir in orderFolders)
        {
            var jobListing = await client.GetListing($"{artworkRoot}/{orderDir.Name}", ct);

            foreach (var jobDir in jobListing.Where(f => f.Type == FtpObjectType.Directory))
            {
                var files = await client.GetListing($"{artworkRoot}/{orderDir.Name}/{jobDir.Name}", ct);

                foreach (var file in files.Where(f => f.Type == FtpObjectType.File))
                {
                    var localPath = Path.Combine(localOrderFolder, "artwork", file.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

                    if (File.Exists(localPath))
                    {
                        downloaded.Add(localPath);
                        continue;
                    }

                    var ftpPath = $"{artworkRoot}/{orderDir.Name}/{jobDir.Name}/{file.Name}";
                    var status = await client.DownloadFile(localPath, ftpPath, token: ct);

                    if (status == FtpStatus.Success)
                    {
                        downloaded.Add(localPath);
                    }
                    else
                    {
                        AlertCollector.Error(AlertCategory.Network,
                            $"FTP download failed for {file.Name}",
                            detail: $"Status: {status}. Remote: {ftpPath}. Local: {localPath}");
                    }
                }
            }
        }

        AppLog.Info($"Downloaded {downloaded.Count} artwork files for {orderNumber}");
        return downloaded;
    }
}
