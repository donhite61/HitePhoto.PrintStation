using System;
using System.IO;

namespace HitePhoto.PrintStation.Core;

/// <summary>
/// Simple file logger. Defaults to %LOCALAPPDATA%\HitePhoto.PrintStation\printstation.log.
/// Call Init(logDirectory) before first use to override the log directory.
/// Call InitNas(updateLocalFolder, storeId) to also write a copy to the NAS.
/// Rolls each file when it exceeds 5 MB.
/// </summary>
public static class AppLog
{
    private static bool _enabled;
    private static readonly object _lock = new();
    private const long MaxFileSize = 5 * 1024 * 1024; // 5 MB

    private static string _logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HitePhoto.PrintStation");

    private static string? _nasLogDir;

    private static string LogPath => Path.Combine(_logDir, "printstation.log");
    private static string? NasLogPath => _nasLogDir != null ? Path.Combine(_nasLogDir, "printstation.log") : null;

    public static void Init(string? logDirectory)
    {
        if (!string.IsNullOrWhiteSpace(logDirectory))
            _logDir = logDirectory;
    }

    /// <summary>
    /// Set a NAS log directory directly.
    /// </summary>
    public static void InitNas(string? nasLogDir)
    {
        if (!string.IsNullOrWhiteSpace(nasLogDir))
            _nasLogDir = nasLogDir;
    }

    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex)
        => Write("ERROR", $"{message}  |  {ex.GetType().Name}: {ex.Message}\n    {ex.StackTrace}");

    private static void Write(string level, string message)
    {
        if (!_enabled) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{level}]  {message}\n";

        lock (_lock)
        {
            WriteToFile(LogPath, line);
            if (NasLogPath != null)
                WriteToFile(NasLogPath, line);
        }
    }

    private static void WriteToFile(string path, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Roll if over limit
            if (File.Exists(path) && new FileInfo(path).Length > MaxFileSize)
            {
                var prev = path + ".1";
                if (File.Exists(prev)) File.Delete(prev);
                File.Move(path, prev);
            }

            File.AppendAllText(path, line);
        }
        catch { /* logging must never crash the app */ }
    }
}
