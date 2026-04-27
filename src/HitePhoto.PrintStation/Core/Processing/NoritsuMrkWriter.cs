using System.IO;
using System.Text;
using HitePhoto.Shared.Models;

namespace HitePhoto.PrintStation.Core.Processing;

/// <summary>
/// Writes AUTPRINT.MRK files in the exact Noritsu AUTOPRINT format.
///
/// Output folder naming: o{externalOrderId}_{sizeLabel}
///   e.g. order 6UWRZk, size 4x6 → folder "o6UWRZk_4x6"
///
/// Staging: folder is created with "p" prefix during file writes,
/// then renamed to "o" prefix atomically when complete.
/// This prevents the Noritsu from grabbing the folder mid-copy.
/// </summary>
public class NoritsuMrkWriter
{
    private readonly string _outputRoot;

    public NoritsuMrkWriter(string outputRoot)
    {
        _outputRoot = outputRoot;
    }

    /// <summary>
    /// Writes a complete Noritsu output folder for one size group of an order.
    /// Takes Order + list of OrderItems (already grouped by size/media by the caller).
    /// Copies images flat into the folder root, writes MISC\AUTPRINT.MRK.
    /// Returns the final MRK file path.
    /// </summary>
    public string WriteMrk(
        Order order,
        string sizeLabel,
        int channelNumber,
        List<OrderItem> items,
        Action<int, int>? onProgress = null)
    {
        if (channelNumber == 0)
            throw new InvalidOperationException(
                $"Channel not assigned for {sizeLabel} in order {order.ExternalOrderId}");

        string baseName = $"{order.ExternalOrderId}_{SanitizeFolderName(sizeLabel)}";
        var (stagingDir, finalDir) = ResolveFolderPair(baseName);
        string miscDir = Path.Combine(stagingDir, "MISC");

        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(miscDir);

        // Build work list sequentially (resolve filenames, check existence)
        var workItems = new List<(OrderItem item, string destFileName, string destPath, int idx)>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int idx = 0;
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.ImageFilepath) || !File.Exists(item.ImageFilepath))
            {
                AlertCollector.Error(AlertCategory.Printing,
                    "Image file missing during MRK write — skipped",
                    orderId: order.ExternalOrderId,
                    detail: $"Attempted: copy '{item.ImageFilepath ?? "(null)"}' for print. " +
                            $"Expected: file exists on disk. Found: missing. " +
                            $"State: order {order.ExternalOrderId}, size {sizeLabel}, item ID {item.Id}.");
                continue;
            }

            idx++;
            string destFileName = Path.GetFileName(item.ImageFilepath);

            // Handle duplicate filenames
            if (!usedNames.Add(destFileName))
            {
                string nameNoExt = Path.GetFileNameWithoutExtension(destFileName);
                string ext = Path.GetExtension(destFileName);
                destFileName = $"{nameNoExt}_{idx}{ext}";
                usedNames.Add(destFileName);
            }

            string destPath = Path.Combine(stagingDir, destFileName);
            workItems.Add((item, destFileName, destPath, idx));
        }

        // Prepare images in parallel (ICC conversion + orient + strip)
        int completed = 0;
        Parallel.ForEach(workItems, work =>
        {
            ImagePreparer.PrepareForPrint(work.item.ImageFilepath, work.destPath);
            int done = Interlocked.Increment(ref completed);
            onProgress?.Invoke(done, workItems.Count);
        });

        var imageEntries = workItems
            .Select(w => (relativePath: $"../{w.destFileName}", originalName: w.destFileName, qty: w.item.Quantity))
            .ToList();

        if (imageEntries.Count == 0)
        {
            // Clean up empty staging folder
            try { Directory.Delete(stagingDir, recursive: true); } catch { }
            throw new InvalidOperationException(
                $"No valid images to print for {sizeLabel} in order {order.ExternalOrderId}");
        }

        string mrkPath = Path.Combine(miscDir, "AUTPRINT.MRK");
        WriteMrkFile(mrkPath, order.ExternalOrderId, sizeLabel, channelNumber, imageEntries);

        // Atomic rename: p → o signals the Noritsu that the folder is complete
        Directory.Move(stagingDir, finalDir);

        string finalMrkPath = Path.Combine(finalDir, "MISC", "AUTPRINT.MRK");
        AppLog.Info($"WriteMrk: o{baseName}, CH {channelNumber:D3}, {imageEntries.Count} images, {imageEntries.Sum(e => e.qty)} prints");
        return finalMrkPath;
    }

    // ── MRK format ────────────────────────────────────────────────────────

    private static void WriteMrkFile(
        string mrkPath,
        string shortId,
        string sizeLabel,
        int channelNumber,
        List<(string relativePath, string originalName, int qty)> images)
    {
        var sb = new StringBuilder();
        string now = DateTime.Now.ToString("yyyy:MM:dd:HH:mm:ss");
        string ch = channelNumber.ToString("D3");

        sb.Append("[HDR]\r\n");
        sb.Append("GEN REV = 01.10\r\n");
        sb.Append("GEN CRT = \"NORITSU KOKI\" -01.10\r\n");
        sb.Append($"GEN DTM = {now} \r\n");
        sb.Append($"USR NAM = \"{shortId}\"\r\n");
        sb.Append($"USR CID = \"{shortId}\"\r\n");
        sb.Append("AUTO CORRECT = 0\r\n");
        sb.Append("VUQ RGN = BGN\r\n");
        sb.Append("VUQ VNM = \"NORITSU KOKI\" -ATR \"QSSPrint\"\r\n");
        sb.Append("VUQ VER = 01.00\r\n");
        sb.Append($"PRT PSL = NML -PSIZE \"{sizeLabel}\"\r\n");
        sb.Append($"PRT PCH = {ch}\r\n");
        sb.Append("GEN INP = \"Other-M\"\r\n");
        sb.Append("VUQ RGN = END\r\n");
        sb.Append("\r\n");

        int pid = 1;
        foreach (var (relativePath, originalName, qty) in images)
        {
            sb.Append("[JOB]\r\n");
            sb.Append($"PRT PID = {pid:D3}\r\n");
            sb.Append("PRT TYP = STD\r\n");
            sb.Append($"PRT QTY = {qty}\r\n");
            sb.Append("IMG FMT = EXIF2 -J\r\n");
            sb.Append($"<IMG SRC = \"{relativePath}\">\r\n");
            sb.Append("VUQ RGN = BGN\r\n");
            sb.Append("VUQ VNM = \"NORITSU KOKI\" -ATR \"QSSPrint\"\r\n");
            sb.Append("VUQ VER = 01.00\r\n");
            sb.Append($"IMG ORG = \"{originalName}\"\r\n");
            sb.Append("VUQ RGN = END\r\n");
            sb.Append("\r\n");
            pid++;
        }

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(mrkPath, sb.ToString(), encoding);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string SanitizeFolderName(string name)
        => name.Replace('.', '_');

    private (string stagingDir, string finalDir) ResolveFolderPair(string baseName)
    {
        string staging = Path.Combine(_outputRoot, $"p{baseName}");
        string final_ = Path.Combine(_outputRoot, $"o{baseName}");
        string done = Path.Combine(_outputRoot, $"e{baseName}");

        // If only a stale staging folder exists, clean it up
        if (Directory.Exists(staging) && !Directory.Exists(final_) && !Directory.Exists(done))
        {
            try { Directory.Delete(staging, recursive: true); }
            catch { }
        }

        if (!Directory.Exists(staging) && !Directory.Exists(final_) && !Directory.Exists(done))
            return (staging, final_);

        // Collision — append suffix
        for (int i = 2; i < 100; i++)
        {
            string suffixed = $"{baseName}_{i}";
            staging = Path.Combine(_outputRoot, $"p{suffixed}");
            final_ = Path.Combine(_outputRoot, $"o{suffixed}");
            done = Path.Combine(_outputRoot, $"e{suffixed}");

            if (!Directory.Exists(staging) && !Directory.Exists(final_) && !Directory.Exists(done))
                return (staging, final_);
        }

        throw new InvalidOperationException($"Cannot find a non-colliding folder name for {baseName}");
    }

}
