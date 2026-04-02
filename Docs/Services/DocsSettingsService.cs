using System;
using System.IO;
using System.Text.Json;
using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

public static class DocsSettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TurboSuite", "TurboDocsSettings.json");

    private static readonly string LegacySettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TurboSuite", "TurboCutsSettings.json");

    public static DocsSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<DocsSettings>(json) ?? new DocsSettings();
            }

            // Migrate from legacy settings file
            if (File.Exists(LegacySettingsPath))
            {
                var json = File.ReadAllText(LegacySettingsPath);
                var settings = JsonSerializer.Deserialize<DocsSettings>(json) ?? new DocsSettings();
                Save(settings);
                return settings;
            }

            return new DocsSettings();
        }
        catch
        {
            return new DocsSettings();
        }
    }

    public static void Save(DocsSettings settings)
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
