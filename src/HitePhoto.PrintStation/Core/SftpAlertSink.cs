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

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AppSettings _settings;
    private readonly string _storeCode;
    private readonly string _remoteAlertsDir;
    private readonly string _remoteLogPath;
    private readonly bool _useSftp;

    private readonly object _lock = new();
    private readonly List<AlertRecord> _batch = new();
    private readonly HashSet<string> _batchSeen = new();
    private readonly Timer _timer;
    private bool _sftpDirectoriesVerified;
    private bool _disposed;

    public SftpAlertSink(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _storeCode = settings.StoreId == 1 ? "BH" : "WB";

        _useSftp = !string.IsNullOrWhiteSpace(settings.UpdateSftpHost)
                && !string.IsNullOrWhiteSpace(settings.UpdateSftpFolder);

        if (_useSftp)
        {
            // Derive from UpdateSftpFolder:
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

        _timer = new Timer(_ => Flush(), null,
            TimeSpan.FromSeconds(FlushIntervalSeconds),
            TimeSpan.FromSeconds(FlushIntervalSeconds));
    }

    public void Persist(AlertRecord record)
    {
        if (!string.Equals(record.Severity, "ERROR", StringComparison.OrdinalIgnoreCase))
            return;

        // Skip self-referential errors to avoid infinite loops
        if (string.Equals(record.Category, "Network", StringComparison.OrdinalIgnoreCase)
            && record.Summary != null
            && record.Summary.Contains("alert upload", StringComparison.OrdinalIgnoreCase))
            return;

        var dedupKey = $"{record.Category}|{record.Summary}|{record.OrderId}";
        lock (_lock)
        {
            if (_batch.Count < MaxBatchSize && _batchSeen.Add(dedupKey))
                _batch.Add(record);
        }
    }

    private void Flush()
    {
        List<AlertRecord> snapshot;
        lock (_lock)
        {
            if (_batch.Count == 0) return;
            snapshot = new List<AlertRecord>(_batch);
            _batch.Clear();
            _batchSeen.Clear();
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{timestamp}_{_storeCode}_{snapshot.Count}alerts.json";
        var json = SerializeReport(snapshot);

        try
        {
            if (_useSftp)
                FlushViaSftp(fileName, json);
            else
                FlushViaNas(fileName, json);
        }
        catch (Exception ex)
        {
            AppLog.Error($"SftpAlertSink flush failed ({snapshot.Count} alerts): {ex.Message}");
        }
    }

    private void FlushViaSftp(string fileName, string json)
    {
        using var client = CreateClient();
        client.Connect();

        if (!_sftpDirectoriesVerified)
        {
            EnsureSftpDirectory(client, _remoteAlertsDir);
            EnsureSftpDirectory(client, Path.GetDirectoryName(_remoteLogPath)!.Replace('\\', '/'));
            _sftpDirectoriesVerified = true;
        }

        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            client.UploadFile(stream, $"{_remoteAlertsDir}/{fileName}");

        var logPath = AppLog.CurrentLogPath;
        if (File.Exists(logPath))
        {
            using var logStream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            client.UploadFile(logStream, _remoteLogPath);
        }

        AppLog.Info($"SftpAlertSink: uploaded alerts + log to {_remoteAlertsDir}/{fileName}");
    }

    private void FlushViaNas(string fileName, string json)
    {
        if (string.IsNullOrWhiteSpace(_remoteAlertsDir)) return;

        Directory.CreateDirectory(_remoteAlertsDir);
        File.WriteAllText(Path.Combine(_remoteAlertsDir, fileName), json);

        AppLog.Info($"SftpAlertSink: wrote alerts to {_remoteAlertsDir}/{fileName}");
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

        return JsonSerializer.Serialize(report, s_jsonOptions);
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

    /// <summary>
    /// Send the current log file to NAS on demand. Tries direct file copy first;
    /// falls back to SFTP if the NAS path isn't reachable.
    /// Returns a status message for the UI.
    /// </summary>
    public static string SendLogsNow(AppSettings settings)
    {
        var logPath = AppLog.CurrentLogPath;
        if (!File.Exists(logPath))
            return "No log file found.";

        var storeCode = settings.StoreId == 1 ? "BH" : "WB";

        // Try NAS direct copy first
        if (!string.IsNullOrWhiteSpace(settings.NasLogFolder))
        {
            try
            {
                Directory.CreateDirectory(settings.NasLogFolder);
                var destPath = Path.Combine(settings.NasLogFolder, "printstation.log");
                using var src = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write);
                src.CopyTo(dst);

                // Also copy rotated log if it exists
                var rotatedPath = logPath + ".1";
                if (File.Exists(rotatedPath))
                {
                    var rotatedDest = Path.Combine(settings.NasLogFolder, "printstation.log.1");
                    File.Copy(rotatedPath, rotatedDest, overwrite: true);
                }

                AppLog.Info($"SendLogsNow: copied log to {destPath}");
                return $"Sent to {destPath}";
            }
            catch (Exception ex)
            {
                AppLog.Info($"SendLogsNow: NAS copy failed ({ex.Message}), trying SFTP...");
            }
        }

        // Fallback: SFTP
        if (string.IsNullOrWhiteSpace(settings.UpdateSftpHost)
            || string.IsNullOrWhiteSpace(settings.UpdateSftpFolder))
            return "NAS path failed and no SFTP configured.";

        try
        {
            var basePath = settings.UpdateSftpFolder;
            var updatesIdx = basePath.IndexOf("/updates/PrintStation", StringComparison.OrdinalIgnoreCase);
            var root = updatesIdx >= 0 ? basePath[..updatesIdx] : basePath;
            var remoteLogPath = $"{root}/logs/PrintStation/{storeCode}/printstation.log";
            var remoteLogDir = $"{root}/logs/PrintStation/{storeCode}";

            using var client = new SftpClient(
                settings.UpdateSftpHost, settings.UpdateSftpPort,
                settings.UpdateSftpUsername, settings.UpdateSftpPassword);
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
            client.OperationTimeout = TimeSpan.FromSeconds(30);
            client.Connect();

            EnsureSftpDirectory(client, remoteLogDir);

            using var logStream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            client.UploadFile(logStream, remoteLogPath);

            client.Disconnect();
            AppLog.Info($"SendLogsNow: uploaded log via SFTP to {remoteLogPath}");
            return $"Sent via SFTP to {remoteLogPath}";
        }
        catch (Exception ex)
        {
            AppLog.Error($"SendLogsNow: SFTP upload failed: {ex.Message}");
            return $"Failed: {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        Flush();
    }
}
