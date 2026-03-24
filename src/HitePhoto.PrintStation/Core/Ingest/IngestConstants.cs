using System.IO;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Constants for ingest pipeline. Single source of truth for marker names and status values.
/// </summary>
public static class IngestConstants
{
    // Marker file names (written to metadata/ subfolder)
    public const string MarkerDownloadComplete = "download_complete";
    public const string MarkerDbSynced = "db_synced";
    public const string MarkerReceivedPushed = "received_pushed";

    // Download status values
    public const string StatusReady = "ready";
    public const string StatusPending = "pending";
    public const string StatusDownloadError = "download_error";

    /// <summary>Writes a timestamp marker file in the metadata/ subfolder.</summary>
    public static void WriteMarker(string orderFolderPath, string markerName)
    {
        var metaDir = Path.Combine(orderFolderPath, "metadata");
        Directory.CreateDirectory(metaDir);
        File.WriteAllText(Path.Combine(metaDir, markerName), DateTime.Now.ToString("O"));
    }

    /// <summary>Checks whether a marker file exists in the metadata/ subfolder.</summary>
    public static bool MarkerExists(string orderFolderPath, string markerName)
        => File.Exists(Path.Combine(orderFolderPath, "metadata", markerName));

    /// <summary>Replaces characters invalid in file/folder names with underscores.</summary>
    public static string SanitizeFolderName(string name)
        => string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();

    /// <summary>Resolves the order folder path from an external order ID and output root.</summary>
    public static string GetOrderFolderPath(string outputRoot, string externalOrderId)
    {
        var shortId = OrderHelpers.GetShortId(externalOrderId);
        return Path.Combine(outputRoot, SanitizeFolderName(shortId));
    }
}
