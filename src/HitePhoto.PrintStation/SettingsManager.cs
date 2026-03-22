using System;
using System.IO;
using System.Text.Json;
using HitePhoto.PrintStation.Core;

namespace HitePhoto.PrintStation;

public class SettingsManager
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "HitePhoto", "PrintStation");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new AppSettings();

        try
        {
            string json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Settings,
                "Failed to load settings, using defaults",
                detail: $"Path: {SettingsPath}",
                ex: ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Settings,
                "Failed to save settings",
                detail: $"Path: {SettingsPath}",
                ex: ex);
        }
    }
}
