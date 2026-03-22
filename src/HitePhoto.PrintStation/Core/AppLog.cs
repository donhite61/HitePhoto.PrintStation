using System;
using System.IO;

namespace HitePhoto.PrintStation.Core;

/// <summary>
/// Simple file logger. Writes to %LOCALAPPDATA%\HitePhoto.PrintStation\printstation.log.
/// Rolls the file when it exceeds 5 MB.
/// </summary>
public static class AppLog
{
    private static bool _enabled;
    private static readonly object _lock = new();
    private const long MaxFileSize = 5 * 1024 * 1024; // 5 MB

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HitePhoto.PrintStation", "printstation.log");

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
        try
        {
            lock (_lock)
            {
                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // Roll if over limit
                if (File.Exists(path) && new FileInfo(path).Length > MaxFileSize)
                {
                    var prev = path + ".1";
                    if (File.Exists(prev)) File.Delete(prev);
                    File.Move(path, prev);
                }

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{level}]  {message}\n";
                File.AppendAllText(path, line);
            }
        }
        catch { /* logging must never crash the app */ }
    }
}
