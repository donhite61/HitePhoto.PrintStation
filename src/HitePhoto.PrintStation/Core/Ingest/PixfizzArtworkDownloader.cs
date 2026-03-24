using System.IO;
using System.Text.Json;
using HitePhoto.Shared.Parsers;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Orchestrates Pixfizz order download: FTP artwork + TXT, parse, verify, folder structure.
/// TXT is the source of truth — if no TXT, the order is not ready.
/// No fallbacks to API-only data.
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
        UnifiedOrder order, RawOrder raw, CancellationToken ct)
    {
        var orderNumber = order.ExternalOrderId;
        var jobId = raw.Metadata?.GetValueOrDefault("job_id") ?? "";
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
            return ArtworkDownloadResult.Failed(order, $"TXT download failed: {ex.Message}");
        }

        if (txtContent == null)
        {
            // No TXT yet — order is not ready. Do not ingest.
            return ArtworkDownloadResult.Failed(order, "Darkroom TXT not found on FTP — order not ready");
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
            return ArtworkDownloadResult.Failed(order, $"Artwork download failed: {ex.Message}");
        }

        // ── Step 3: Parse TXT (source of truth) ──
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
            return ArtworkDownloadResult.Failed(order, "TXT file could not be parsed");
        }

        var txtItems = TxtItemConverter.ToUnifiedItems(txtResult);

        // ── Step 4: Move files to Dakis-compatible structure + verify ──
        int verifiedCount = 0;
        var rewrittenItems = new List<UnifiedOrderItem>();

        foreach (var item in txtItems)
        {
            if (string.IsNullOrEmpty(item.ImageFilepath) || !File.Exists(item.ImageFilepath))
            {
                errors.Add($"File not found: {item.ImageFilename}");
                rewrittenItems.Add(item);
                continue;
            }

            // Build Dakis folder structure: prints/{format} format/{qty} prints/
            var dakisDir = Path.Combine(localFolder, "prints",
                $"{item.FormatString ?? item.SizeLabel} format",
                $"{item.Quantity} prints");
            Directory.CreateDirectory(dakisDir);

            var dakisPath = Path.Combine(dakisDir, item.ImageFilename ?? Path.GetFileName(item.ImageFilepath));
            if (!File.Exists(dakisPath))
            {
                try
                {
                    File.Move(item.ImageFilepath, dakisPath);
                }
                catch (Exception ex)
                {
                    errors.Add($"Cannot move {item.ImageFilename}: {ex.Message}");
                    rewrittenItems.Add(item);
                    continue;
                }
            }

            // Verify JPEG: exists, >1KB, magic bytes
            var verifyError = OrderHelpers.VerifyFile(dakisPath);
            if (verifyError != null)
            {
                // Delete bad file and retry from FTP once
                try { File.Delete(dakisPath); } catch { }

                var retryFiles = await RetryDownloadAsync(orderNumber, jobId, localFolder, ct);
                var retryPath = retryFiles?.FirstOrDefault(f =>
                    Path.GetFileName(f).Equals(item.ImageFilename, StringComparison.OrdinalIgnoreCase));

                if (retryPath != null && File.Exists(retryPath))
                    try { File.Move(retryPath, dakisPath, overwrite: true); } catch { }

                verifyError = OrderHelpers.VerifyFile(dakisPath);
                if (verifyError != null)
                {
                    errors.Add($"{item.ImageFilename}: {verifyError}");
                    rewrittenItems.Add(item with { ImageFilepath = dakisPath });
                    continue;
                }
            }

            verifiedCount++;
            rewrittenItems.Add(item with { ImageFilepath = dakisPath });
        }

        // ── Step 5: Fill order from TXT (source of truth — no API data used) ──
        order = PixfizzOrderParser.FillFromTxt(order, txtResult) with
        {
            FolderPath = localFolder,
            Items = rewrittenItems,
            DownloadStatus = errors.Count == 0 ? IngestConstants.StatusReady : IngestConstants.StatusDownloadError,
            DownloadErrors = errors
        };

        // ── Step 6: Save supplementary files ──
        try
        {
            var rawPath = Path.Combine(localFolder, "pixfizz_raw.json");
            if (!File.Exists(rawPath))
                await File.WriteAllTextAsync(rawPath, raw.RawData, ct);
        }
        catch (Exception ex)
        {
            AlertCollector.Warn(AlertCategory.General,
                $"Could not write pixfizz_raw.json for {orderNumber}", ex: ex);
        }

        return new ArtworkDownloadResult
        {
            Order = order,
            TxtFound = true,
            TxtParsed = true,
            ExpectedImageCount = txtItems.Count,
            VerifiedImageCount = verifiedCount,
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
            AlertCollector.Warn(AlertCategory.Network,
                $"Retry download failed for {orderNumber}", ex: ex);
            return null;
        }
    }
}

public class ArtworkDownloadResult
{
    public required UnifiedOrder Order { get; init; }
    public bool TxtFound { get; init; }
    public bool TxtParsed { get; init; }
    public int ExpectedImageCount { get; init; }
    public int VerifiedImageCount { get; init; }
    public List<string> Errors { get; init; } = [];

    public bool Success => TxtParsed && Errors.Count == 0;

    public static ArtworkDownloadResult Failed(UnifiedOrder order, string reason) => new()
    {
        Order = order with
        {
            DownloadStatus = IngestConstants.StatusDownloadError,
            DownloadErrors = [reason]
        },
        TxtFound = false,
        TxtParsed = false,
        Errors = [reason]
    };
}
