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
    public const string StatusReady = "ready";                 // manifest fetched + files downloaded
    public const string StatusPending = "pending";             // legacy generic — prefer the more specific values below
    public const string StatusUnpaid = "unpaid";               // paid==false; Pixfizz withholds artwork until paid
    public const string StatusAwaitingFiles = "awaiting_files"; // paid, but Pixfizz hasn't emitted the JSON manifest yet (template config gap)
    public const string StatusNoArtworkExpected = "no_artwork_expected"; // paid, but no artwork is coming (e.g. Film Processing — lab does the work)
    public const string StatusDownloadError = "download_error";

    // OHD API enum values (referenced from PixfizzApiJsonParser + PixfizzPathHelpers)
    public const string OhdOrderStatusConfirmed = "confirmed";        // paid + ready to fulfill
    public const string CategoryFilmProcessing  = "Film Processing";  // lab-side workflow, no artwork download expected
    public const string ProcessNoritsu          = "Noritsu";          // job routes to the Noritsu printer

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
        return Path.Combine(outputRoot, SanitizeFolderName(externalOrderId));
    }
}
