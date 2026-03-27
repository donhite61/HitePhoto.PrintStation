using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core;

// ── Enums ────────────────────────────────────────────────────────────────

public enum AlertSeverity { Error, Warning, Info }

public enum AlertCategory
{
    Parsing,      // YML, TXT, JSON parsing errors
    DataQuality,  // Missing/invalid fields on orders
    Printing,     // Noritsu MRK, printer output, layout
    Network,      // SFTP, HTTP — any remote connection
    Database,     // MariaDB/SQLite queries
    Settings,     // Config issues, missing paths
    Update,       // Auto-updater
    General       // Catch-all
}

// ── Alert model ──────────────────────────────────────────────────────────

public class AppAlert
{
    // ── Operator-facing (shown in the alert window) ──
    public AlertSeverity Severity    { get; init; }
    public AlertCategory Category    { get; init; }
    public string        Summary     { get; init; } = "";   // Plain English, no jargon
    public string?       OrderId     { get; init; }
    public DateTime      Timestamp   { get; init; } = DateTime.Now;

    // ── Technical (hidden by default, expandable, included in clipboard copy) ──
    public string?  Method     { get; init; }   // CallerMemberName
    public string?  SourceFile { get; init; }   // CallerFilePath (filename only)
    public int      SourceLine { get; init; }   // CallerLineNumber
    public string?  Detail     { get; init; }   // Free-form context (URLs, field values, raw data)
    public string?  Exception  { get; init; }   // ex.ToString() — full stack trace

    // ── Display helpers ──
    public string SeverityLabel => Severity switch
    {
        AlertSeverity.Error   => "ERROR",
        AlertSeverity.Warning => "WARN",
        _                     => "INFO"
    };

    public string CategoryLabel => Category.ToString();

    public string DisplayId => string.IsNullOrEmpty(OrderId) ? "—" : OrderId;

    /// <summary>Full technical dump for clipboard / log file.</summary>
    public string TechnicalDump()
    {
        var parts = new List<string>();
        parts.Add($"[{SeverityLabel}] [{CategoryLabel}] {Timestamp:yyyy-MM-dd HH:mm:ss}");
        if (!string.IsNullOrEmpty(OrderId)) parts.Add($"Order: {OrderId}");
        parts.Add($"Summary: {Summary}");
        if (!string.IsNullOrEmpty(Method))
            parts.Add($"Location: {Method} ({SourceFile}:{SourceLine})");
        if (!string.IsNullOrEmpty(Detail)) parts.Add($"Detail: {Detail}");
        if (!string.IsNullOrEmpty(Exception)) parts.Add($"Exception:\n{Exception}");
        return string.Join("\n", parts);
    }
}

// ── Collector ────────────────────────────────────────────────────────────

/// <summary>
/// Central alert hub for the entire application. Thread-safe.
/// Any subsystem can post alerts. The UI drains and displays them periodically.
/// </summary>
public static class AlertCollector
{
    private static readonly object _lock = new();
    private static readonly List<AppAlert> _alerts = new();
    private static readonly HashSet<string> _seen = new();
    private static IAlertRepository? _repository;

    /// <summary>
    /// Set the persistence repository. Called once at startup after DI is built.
    /// If null, alerts still work in-memory — persistence is gracefully skipped.
    /// </summary>
    public static void SetRepository(IAlertRepository repository) => _repository = repository;

    // ── Core Add ─────────────────────────────────────────────────────────

    public static void Add(AppAlert alert)
    {
        // Dedup key: same category + summary + order = same alert.
        // First occurrence goes to the UI queue. Repeats are logged + persisted only.
        var dedupKey = $"{alert.Category}|{alert.Summary}|{alert.OrderId}";
        bool isNew;

        lock (_lock)
        {
            isNew = _seen.Add(dedupKey);
            if (isNew)
            {
                if (_alerts.Count >= 10_000) _alerts.RemoveAt(0);
                _alerts.Add(alert);
            }
        }

        // ALWAYS include ALL data in every log entry — exception is never optional.
        var logMsg = $"Alert [{alert.SeverityLabel}] [{alert.CategoryLabel}]" +
            (string.IsNullOrEmpty(alert.OrderId) ? "" : $" Order={alert.OrderId}") +
            $" {alert.Summary}" +
            (string.IsNullOrEmpty(alert.Detail) ? "" : $" | {alert.Detail}") +
            (string.IsNullOrEmpty(alert.Method) ? "" : $" | at {alert.Method} ({alert.SourceFile}:{alert.SourceLine})") +
            (alert.Exception != null ? $"\n{alert.Exception}" : "");

        switch (alert.Severity)
        {
            case AlertSeverity.Error:   AppLog.Error(logMsg); break;
            case AlertSeverity.Warning: AppLog.Warn(logMsg);  break;
            default:                    AppLog.Info(logMsg);   break;
        }

        // Persist errors and warnings to SQLite
        if (_repository != null && alert.Severity != AlertSeverity.Info)
        {
            try
            {
                _repository.Insert(new AlertRecord(
                    Id: 0,
                    Severity: alert.SeverityLabel,
                    Category: alert.CategoryLabel,
                    Summary: alert.Summary,
                    OrderId: alert.OrderId,
                    Detail: alert.Detail,
                    Exception: alert.Exception,
                    SourceMethod: alert.Method,
                    SourceFile: alert.SourceFile,
                    SourceLine: alert.SourceLine,
                    CreatedAt: alert.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    Acknowledged: false));
            }
            catch (Exception ex)
            {
                // Don't let persistence failure break the alert system
                AppLog.Error($"Failed to persist alert to SQLite: {ex.Message}");
            }
        }
    }

    // ── Convenience methods (auto-capture caller info) ───────────────────

    public static void Error(AlertCategory category, string summary,
        string? orderId = null, string? detail = null, Exception? ex = null,
        [CallerMemberName] string? method = null,
        [CallerFilePath]  string? file = null,
        [CallerLineNumber] int line = 0)
    {
        Add(new AppAlert
        {
            Severity   = AlertSeverity.Error,
            Category   = category,
            Summary    = summary,
            OrderId    = orderId,
            Detail     = detail,
            Exception  = ex?.ToString(),
            Method     = method,
            SourceFile = Path.GetFileName(file),
            SourceLine = line
        });
    }

    public static void Warn(AlertCategory category, string summary,
        string? orderId = null, string? detail = null, Exception? ex = null,
        [CallerMemberName] string? method = null,
        [CallerFilePath]  string? file = null,
        [CallerLineNumber] int line = 0)
    {
        Add(new AppAlert
        {
            Severity   = AlertSeverity.Warning,
            Category   = category,
            Summary    = summary,
            OrderId    = orderId,
            Detail     = detail,
            Exception  = ex?.ToString(),
            Method     = method,
            SourceFile = Path.GetFileName(file),
            SourceLine = line
        });
    }

    public static void Info(AlertCategory category, string summary,
        string? orderId = null, string? detail = null,
        [CallerMemberName] string? method = null,
        [CallerFilePath]  string? file = null,
        [CallerLineNumber] int line = 0)
    {
        Add(new AppAlert
        {
            Severity   = AlertSeverity.Info,
            Category   = category,
            Summary    = summary,
            OrderId    = orderId,
            Detail     = detail,
            Method     = method,
            SourceFile = Path.GetFileName(file),
            SourceLine = line
        });
    }

    // ── Drain ────────────────────────────────────────────────────────────

    public static List<AppAlert> GetAndClear()
    {
        lock (_lock)
        {
            var copy = new List<AppAlert>(_alerts);
            _alerts.Clear();
            return copy;
        }
    }

    public static int Count { get { lock (_lock) return _alerts.Count; } }

    public static void Clear() { lock (_lock) { _alerts.Clear(); _seen.Clear(); } }
}
