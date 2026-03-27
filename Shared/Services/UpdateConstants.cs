using System;
using System.IO;
using System.Text.Json;

namespace TurboSuite.Shared.Services;

public static class UpdateConstants
{
    public const int CheckTimeoutMs = 3000;

    private static string? _serverPath;

    public static string ServerPath => _serverPath ??= LoadServerPath();

    private static string LoadServerPath()
    {
        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TurboSuite", "config.json");

            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("ServerPath", out var prop))
                    return prop.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // Fall through to empty
        }

        return string.Empty;
    }
}
