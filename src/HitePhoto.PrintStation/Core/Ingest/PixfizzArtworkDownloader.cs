using System.IO;
using HitePhoto.Shared.Parsers;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Downloads Pixfizz artwork from FTP and organizes into prints/ folder structure.
/// Download only — no order building. The parser handles that after download.
/// </summary>
public class PixfizzArtworkDownloader
{
    private readonly PixfizzFtpDownloader _ftp;
    private readonly AppSettings _settings;

    public PixfizzArtworkDownloader(PixfizzFtpDownloader ftp, AppSettings settings)
    {
        _ftp = ftp ?? throw new ArgumentNullException(nameof(ftp));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<ArtworkDownloadResult> DownloadAsync(
        string orderNumber, string jobId, CancellationToken ct)
    {
        var localFolder = IngestConstants.GetOrderFolderPath(_settings.OrderOutputPath, orderNumber);
        Directory.CreateDirectory(localFolder);

        var errors = new List<string>();

        // ── Step 1: Download darkroom TXT ──
        string? txtContent;
        try
        {
            txtContent = await _ftp.DownloadDarkroomTxtAsync(orderNumber, ct, jobId);
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Network,
                $"Darkroom TXT download failed for {orderNumber}",
                orderId: orderNumber, ex: ex);
            return ArtworkDownloadResult.Failed(orderNumber, localFolder, $"TXT download failed: {ex.Message}");
        }

        if (txtContent == null)
        {
            return ArtworkDownloadResult.Failed(orderNumber, localFolder, "Darkroom TXT not found on FTP — order not ready");
        }

        // Save TXT to disk
        var ticketPath = Path.Combine(localFolder, "darkroom_ticket.txt");
        await File.WriteAllTextAsync(ticketPath, txtContent, ct);

        // ── Step 2: Download artwork files ──
        List<string> downloadedFiles;
        try
        {
            downloadedFiles = await _ftp.DownloadArtworkAsync(orderNumber, jobId, localFolder, ct);
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Network,
                $"Artwork download failed for {orderNumber}",
                orderId: orderNumber, ex: ex);
            return ArtworkDownloadResult.Failed(orderNumber, localFolder, $"Artwork download failed: {ex.Message}");
        }

        // ── Step 3: Parse TXT to get file-to-size mapping for folder organization ──
        var fileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in downloadedFiles)
            fileMap[Path.GetFileName(f)] = f;

        var txtResult = PixfizzTxtParser.ParseContent(txtContent, ftpPath =>
        {
            var fileName = Path.GetFileName(ftpPath);
            return fileMap.TryGetValue(fileName, out var sourcePath) ? sourcePath : ftpPath;
        });

        if (txtResult == null)
        {
            return ArtworkDownloadResult.Failed(orderNumber, localFolder, "TXT file could not be parsed");
        }

        var txtItems = TxtItemConverter.ToUnifiedItems(txtResult);

        // ── Step 4: Move files to prints/ structure + verify ──
        foreach (var item in txtItems)
        {
            if (string.IsNullOrEmpty(item.ImageFilepath) || !File.Exists(item.ImageFilepath))
            {
                errors.Add($"File not found: {item.ImageFilename}");
                continue;
            }

            var targetDir = Path.Combine(localFolder, "prints",
                $"{item.FormatString ?? item.SizeLabel} format",
                $"{item.Quantity} prints");
            Directory.CreateDirectory(targetDir);

            var targetPath = Path.Combine(targetDir, item.ImageFilename ?? Path.GetFileName(item.ImageFilepath));
            if (!File.Exists(targetPath))
            {
                try
                {
                    File.Move(item.ImageFilepath, targetPath);
                }
                catch (Exception ex)
                {
                    errors.Add($"Cannot move {item.ImageFilename}: {ex.Message}");
                    continue;
                }
            }

            // Verify JPEG: exists, >1KB, magic bytes
            var verifyError = OrderHelpers.VerifyFile(targetPath);
            if (verifyError != null)
            {
                // Delete bad file and retry from FTP once
                try { File.Delete(targetPath); } catch { }

                var retryFiles = await RetryDownloadAsync(orderNumber, jobId, localFolder, ct);
                var retryPath = retryFiles?.FirstOrDefault(f =>
                    Path.GetFileName(f).Equals(item.ImageFilename, StringComparison.OrdinalIgnoreCase));

                if (retryPath != null && File.Exists(retryPath))
                    try { File.Move(retryPath, targetPath, overwrite: true); } catch { }

                verifyError = OrderHelpers.VerifyFile(targetPath);
                if (verifyError != null)
                {
                    errors.Add($"{item.ImageFilename}: {verifyError}");
                    continue;
                }
            }
        }

        return new ArtworkDownloadResult
        {
            OrderNumber = orderNumber,
            FolderPath = localFolder,
            Success = errors.Count == 0,
            Errors = errors
        };
    }

    private async Task<List<string>?> RetryDownloadAsync(
        string orderNumber, string jobId, string localFolder, CancellationToken ct)
    {
        try
        {
            return await _ftp.DownloadArtworkAsync(orderNumber, jobId, localFolder, ct);
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Network,
                $"Retry download failed for {orderNumber}", ex: ex);
            return null;
        }
    }
}

public class ArtworkDownloadResult
{
    public required string OrderNumber { get; init; }
    public required string FolderPath { get; init; }
    public bool Success { get; init; }
    public List<string> Errors { get; init; } = [];

    public static ArtworkDownloadResult Failed(string orderNumber, string folderPath, string reason) => new()
    {
        OrderNumber = orderNumber,
        FolderPath = folderPath,
        Success = false,
        Errors = [reason]
    };
}
