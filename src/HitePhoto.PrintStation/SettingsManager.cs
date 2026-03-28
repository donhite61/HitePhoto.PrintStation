using System;
using System.IO;
using System.Text.Json;
using HitePhoto.PrintStation.Core;

namespace HitePhoto.PrintStation;

public class SettingsManager
{
    private readonly string _settingsDir;
    private readonly string _settingsPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public SettingsManager(string? profile = null)
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HitePhoto", "PrintStation");

        _settingsDir = profile == null
            ? baseDir
            : Path.Combine(baseDir, "profiles", profile);

        _settingsPath = Path.Combine(_settingsDir, "settings.json");
    }

    public string SettingsPath => _settingsPath;

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
            return new AppSettings();

        try
        {
            string json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

            // Migrate: old settings had UpdateLocalFolder — now called NasRootFolder (same path)
            if (string.IsNullOrWhiteSpace(settings.NasRootFolder))
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("UpdateLocalFolder", out var oldProp))
                {
                    var oldPath = oldProp.GetString();
                    if (!string.IsNullOrWhiteSpace(oldPath))
                        settings.NasRootFolder = oldPath;
                }
            }

            return settings;
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Settings,
                "Failed to load settings, using defaults",
                detail: $"Path: {_settingsPath}",
                ex: ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_settingsDir);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Settings,
                "Failed to save settings",
                detail: $"Path: {_settingsPath}",
                ex: ex);
        }
    }
}
