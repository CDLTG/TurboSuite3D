using System;
using System.IO;
using System.Text.Json;
using TurboSuite.Cuts.Models;

namespace TurboSuite.Cuts.Services;

public static class CutsSettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TurboSuite", "TurboCutsSettings.json");

    public static CutsSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new CutsSettings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<CutsSettings>(json) ?? new CutsSettings();
        }
        catch
        {
            return new CutsSettings();
        }
    }

    public static void Save(CutsSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort persistence — don't crash Revit over a settings file.
        }
    }
}
