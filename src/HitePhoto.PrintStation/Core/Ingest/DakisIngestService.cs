using System.IO;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Dakis ingest pipeline. FileSystemWatcher triggers on new folders,
/// debounce gives Dakis time to finish writing, then parse + insert + verify.
/// Timer fallback catches anything the watcher missed.
/// </summary>
public class DakisIngestService : IDisposable
{
    private readonly DakisOrderParser _parser;
    private readonly IngestOrderWriter _writer;
    private readonly AppSettings _settings;

    private FileSystemWatcher? _watcher;

    public DakisIngestService(
        DakisOrderParser parser,
        IngestOrderWriter writer,
        AppSettings settings)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Start watching the Dakis folder for new order directories.
    /// Call once at startup after settings are loaded.
    /// </summary>
    public void StartWatching()
    {
        StopWatching();

        if (!_settings.DakisEnabled) return;

        var watchFolder = _settings.DakisWatchFolder;
        if (string.IsNullOrEmpty(watchFolder) || !Directory.Exists(watchFolder)) return;

        // Watch for download_complete marker files in order subdirectories
        _watcher = new FileSystemWatcher(watchFolder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName,
            Filter = IngestConstants.MarkerDownloadComplete
        };
        _watcher.Created += OnMarkerCreated;
        _watcher.EnableRaisingEvents = true;

        AppLog.Info($"Dakis watcher started: {watchFolder}");
    }

    public void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    /// <summary>
    /// Triggered when download_complete marker appears in an order's metadata folder.
    /// Path: {watchFolder}/{order folder}/metadata/download_complete
    /// Walk up to the order folder and process it.
    /// </summary>
    private void OnMarkerCreated(object sender, FileSystemEventArgs e)
    {
        try
        {
            // e.FullPath = .../order 61005406/metadata/download_complete
            var metadataDir = Path.GetDirectoryName(e.FullPath);
            if (metadataDir == null) return;
            var orderFolder = Path.GetDirectoryName(metadataDir);
            if (orderFolder == null || !Directory.Exists(orderFolder)) return;

            ProcessOrderFolder(orderFolder);
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Parsing,
                $"Failed to process Dakis folder from watcher: {e.FullPath}",
                ex: ex);
        }
    }

    /// <summary>
    /// Full scan of the Dakis watch folder. Timer fallback in case watcher misses an event.
    /// </summary>
    public void ScanFolder()
    {
        if (!_settings.DakisEnabled) return;

        var watchFolder = _settings.DakisWatchFolder;
        if (string.IsNullOrEmpty(watchFolder) || !Directory.Exists(watchFolder))
            return;

        foreach (var orderDir in Directory.GetDirectories(watchFolder))
        {
            try
            {
                ProcessOrderFolder(orderDir);
            }
            catch (Exception ex)
            {
                AlertCollector.Error(AlertCategory.Parsing,
                    $"Failed to process Dakis folder: {Path.GetFileName(orderDir)}",
                    orderId: Path.GetFileName(orderDir), ex: ex);
            }
        }
    }

    private void ProcessOrderFolder(string folderPath)
    {
        // Check for download_complete marker — Dakis is still writing if absent
        if (!IngestConstants.MarkerExists(folderPath, IngestConstants.MarkerDownloadComplete))
            return;

        var orderId = Path.GetFileName(folderPath);

        var ymlPath = Path.Combine(folderPath, "order.yml");
        if (!File.Exists(ymlPath))
        {
            AlertCollector.Error(AlertCategory.Parsing,
                $"order.yml not found in {orderId}",
                orderId: orderId);
            return;
        }

        var ymlContent = File.ReadAllText(ymlPath);

        var raw = new RawOrder(
            ExternalOrderId: orderId,
            SourceName: "dakis",
            RawData: ymlContent,
            Metadata: new Dictionary<string, string> { ["folder_path"] = folderPath });

        var order = _parser.Parse(raw);

        // File verification happens in verify after insert — not here
        order = order with { DownloadStatus = IngestConstants.StatusReady };

        _writer.WriteToSqlite(order, _settings.StoreId, "dakis", order.FolderPath ?? "");
    }

    public void Dispose()
    {
        StopWatching();
        GC.SuppressFinalize(this);
    }
}
