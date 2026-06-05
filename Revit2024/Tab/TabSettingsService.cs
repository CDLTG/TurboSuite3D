using System;
using System.IO;
using System.Text.Json;

namespace TurboSuite.Tab;

public static class TabSettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TurboSuite", "TurboTabSettings.json");

    public static bool LoadEnabled()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return true;
            var json = File.ReadAllText(SettingsPath);
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("Enabled", out var prop) ? prop.GetBoolean() : true;
        }
        catch
        {
            return true;
        }
    }

    public static void SaveEnabled(bool enabled)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new { Enabled = enabled });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort persistence — don't crash Revit over a settings file.
        }
    }
}
