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

    public static AsyncFtpClient CreateClient(AppSettings settings)
    {
        var client = new AsyncFtpClient(
            settings.PixfizzFtpServer,
            settings.PixfizzFtpUsername,
            settings.PixfizzFtpPassword,
            settings.PixfizzFtpPort);
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        client.Config.EncryptionMode = FtpEncryptionMode.None;
        client.Config.ConnectTimeout = 15000;
        client.Config.ReadTimeout = 15000;
        client.Config.DataConnectionConnectTimeout = 15000;
        return client;
    }

    /// <summary>
    /// Downloads the darkroom TXT file for an order from FTP.
    /// The TXT filename uses the orderId (not jobId): "{orderNumber}_{orderId}.txt".
    /// Falls back to matching by orderNumber prefix if orderId doesn't match.
    /// Returns TXT content or null if not found.
    /// </summary>
    public async Task<string?> DownloadDarkroomTxtAsync(string orderNumber, CancellationToken ct, string? orderId = null)
    {
        using var client = CreateClient(_settings);
        await client.Connect(ct);

        var remotePath = _settings.PixfizzFtpDarkroomFolder.TrimEnd('/');
        var listing = await client.GetListing(remotePath, ct);

        var txtFiles = listing.Where(f =>
            f.Type == FtpObjectType.File &&
            f.Name.StartsWith(orderNumber, StringComparison.OrdinalIgnoreCase) &&
            f.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).ToList();

        FtpListItem? match = null;

        // Try exact match with orderId first
        if (!string.IsNullOrEmpty(orderId))
        {
            var expectedName = $"{orderNumber}_{orderId}.txt";
            match = txtFiles.FirstOrDefault(f =>
                f.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase));
        }

        // Fall back to any TXT matching the order number
        if (match == null && txtFiles.Count == 1)
            match = txtFiles[0];
        else if (match == null && txtFiles.Count > 1)
        {
            // Multiple TXT files — take the most recent
            match = txtFiles.OrderByDescending(f => f.Modified).First();
            AppLog.Info($"Pixfizz: multiple TXT files for {orderNumber}, using {match.Name}");
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
        using var client = CreateClient(_settings);
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
