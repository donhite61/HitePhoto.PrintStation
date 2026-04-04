using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using HitePhoto.PrintStation.Data.Repositories;
using Renci.SshNet;

namespace HitePhoto.PrintStation.Core;

/// <summary>
/// Alert sink that batches Error-level alerts and uploads JSON reports + the current
/// log file to the NAS via SFTP (BH) or direct file write (WB).
/// Flushes at most once per <see cref="FlushIntervalSeconds"/> seconds.
/// Self-referential SFTP errors are skipped to avoid infinite loops.
/// </summary>
public sealed class SftpAlertSink : IAlertSink, IDisposable
{
    private const int FlushIntervalSeconds = 60;
    private const int MaxBatchSize = 200;

    private readonly AppSettings _settings;
    private readonly string _storeCode;
    private readonly string _localLogPath;
    private readonly string _remoteAlertsDir;
    private readonly string _remoteLogPath;
    private readonly bool _useSftp;

    private readonly object _lock = new();
    private readonly List<AlertRecord> _batch = new();
    private readonly Timer _timer;
    private bool _disposed;

    /// <param name="settings">App settings (SFTP credentials, NAS paths, StoreId).</param>
    public SftpAlertSink(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _storeCode = settings.StoreId == 1 ? "BH" : "WB";

        // Local log file path (same as AppLog default)
        _localLogPath = Path.Combine(
            string.IsNullOrWhiteSpace(settings.LogDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HitePhoto.PrintStation")
                : settings.LogDirectory,
            "printstation.log");

        // Determine if we need SFTP or can write directly to NAS
        _useSftp = !string.IsNullOrWhiteSpace(settings.UpdateSftpHost)
                && !string.IsNullOrWhiteSpace(settings.UpdateSftpFolder);

        if (_useSftp)
        {
            // Derive log/alert paths from UpdateSftpFolder:
            //   /EMPLOYEES SAVE!!!/Don/StoreManagementSoftware/updates/PrintStation
            // → /EMPLOYEES SAVE!!!/Don/StoreManagementSoftware/logs/PrintStation/{StoreCode}
            var basePath = settings.UpdateSftpFolder;
            var updatesIdx = basePath.IndexOf("/updates/PrintStation", StringComparison.OrdinalIgnoreCase);
            string root = updatesIdx >= 0 ? basePath[..updatesIdx] : basePath;

            _remoteAlertsDir = $"{root}/logs/PrintStation/{_storeCode}/alerts";
            _remoteLogPath = $"{root}/logs/PrintStation/{_storeCode}/printstation.log";
        }
        else
        {
            // WB — write directly to NAS share
            var nasBase = settings.NasLogFolder;
            if (string.IsNullOrWhiteSpace(nasBase))
            {
                _remoteAlertsDir = "";
                _remoteLogPath = "";
            }
            else
            {
                _remoteAlertsDir = Path.Combine(nasBase, "alerts");
                _remoteLogPath = Path.Combine(nasBase, "printstation.log");
            }
        }

        // Flush timer — fires every FlushIntervalSeconds
        _timer = new Timer(_ => Flush(), null,
            TimeSpan.FromSeconds(FlushIntervalSeconds),
            TimeSpan.FromSeconds(FlushIntervalSeconds));
    }

    public void Persist(AlertRecord record)
    {
        // Only batch Error-level alerts
        if (!string.Equals(record.Severity, "Error", StringComparison.OrdinalIgnoreCase))
            return;

        // Skip self-referential SFTP/upload errors to avoid infinite loops
        if (string.Equals(record.Category, "Network", StringComparison.OrdinalIgnoreCase)
            && record.Summary != null
            && record.Summary.Contains("alert upload", StringComparison.OrdinalIgnoreCase))
            return;

        lock (_lock)
        {
            if (_batch.Count < MaxBatchSize)
                _batch.Add(record);
        }
    }

    /// <summary>Drain the batch and upload. Called by timer and on dispose.</summary>
    private void Flush()
    {
        List<AlertRecord> snapshot;
        lock (_lock)
        {
            if (_batch.Count == 0) return;
            snapshot = new List<AlertRecord>(_batch);
            _batch.Clear();
        }

        try
        {
            if (_useSftp)
                FlushViaSftp(snapshot);
            else
                FlushViaNas(snapshot);
        }
        catch (Exception ex)
        {
            // Log locally only — do NOT feed back into AlertCollector (would loop)
            AppLog.Error($"SftpAlertSink flush failed ({snapshot.Count} alerts): {ex.Message}");
        }
    }

    private void FlushViaSftp(List<AlertRecord> alerts)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{timestamp}_{_storeCode}_{alerts.Count}alerts.json";
        var json = SerializeReport(alerts);

        using var client = CreateClient();
        client.Connect();

        // Ensure directories exist
        EnsureSftpDirectory(client, _remoteAlertsDir);
        EnsureSftpDirectory(client, Path.GetDirectoryName(_remoteLogPath)!.Replace('\\', '/'));

        // Upload alert report
        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
        {
            client.UploadFile(stream, $"{_remoteAlertsDir}/{fileName}");
        }

        // Upload current log file
        if (File.Exists(_localLogPath))
        {
            using var logStream = new FileStream(_localLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            client.UploadFile(logStream, _remoteLogPath);
        }

        AppLog.Info($"SftpAlertSink: uploaded {alerts.Count} alerts + log to {_remoteAlertsDir}/{fileName}");
    }

    private void FlushViaNas(List<AlertRecord> alerts)
    {
        if (string.IsNullOrWhiteSpace(_remoteAlertsDir)) return;

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{timestamp}_{_storeCode}_{alerts.Count}alerts.json";
        var json = SerializeReport(alerts);

        Directory.CreateDirectory(_remoteAlertsDir);

        File.WriteAllText(Path.Combine(_remoteAlertsDir, fileName), json);

        // Log file is already written to NAS by AppLog.InitNas — no extra copy needed
        AppLog.Info($"SftpAlertSink: wrote {alerts.Count} alerts to {_remoteAlertsDir}/{fileName}");
    }

    private string SerializeReport(List<AlertRecord> alerts)
    {
        var report = new
        {
            store = _storeCode,
            storeId = _settings.StoreId,
            machine = Environment.MachineName,
            generatedAt = DateTime.Now.ToString("o"),
            alertCount = alerts.Count,
            alerts = alerts
        };

        return JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private SftpClient CreateClient()
    {
        var client = new SftpClient(
            _settings.UpdateSftpHost,
            _settings.UpdateSftpPort,
            _settings.UpdateSftpUsername,
            _settings.UpdateSftpPassword);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        client.OperationTimeout = TimeSpan.FromSeconds(30);
        return client;
    }

    /// <summary>Recursively create remote directories if they don't exist.</summary>
    private static void EnsureSftpDirectory(SftpClient client, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/") return;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var part in parts)
        {
            current += "/" + part;
            if (!client.Exists(current))
                client.CreateDirectory(current);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        Flush(); // drain any remaining alerts
    }
}
