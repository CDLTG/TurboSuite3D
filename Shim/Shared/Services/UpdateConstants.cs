using System;
using System.IO;
using System.Text.Json;

namespace TurboSuite.Shared.Services;

/// <summary>
/// Auto-update configuration loaded from <c>%LOCALAPPDATA%\TurboSuite\{RevitVersion}\config.json</c>.
/// <see cref="ServerPath"/> is the network share scanned by <see cref="UpdateService"/> on Revit launch.
/// </summary>
/// <remarks>
/// All local state is isolated per Revit version because most machines run several
/// Revit versions side by side. <see cref="RevitVersion"/> must be set once at startup
/// (from <c>UIControlledApplication.ControlledApplication.VersionNumber</c>) before any
/// path-dependent member is touched. The shared shim source compiles identically into
/// every per-version DLL — the running Revit, not a compile constant, supplies the year.
/// </remarks>
public static class UpdateConstants
{
    // Per-attempt wait for the server version check. Generous because the first touch of a cold SMB
    // share at Revit launch pays the full Windows SMB connect/auth cost (~30-60s worst case); a short
    // window made the check lose the race and silently skip updates. Off the UI thread, so it never
    // blocks Revit. The caller also retries (see OnIdlingCheckForUpdate), so this is the per-try ceiling.
    public const int CheckTimeoutMs = 30000;

    /// <summary>The running Revit version ("2024", "2025", …). Set once in OnStartup.</summary>
    public static string? RevitVersion { get; set; }

    /// <summary><c>%LOCALAPPDATA%\TurboSuite\{RevitVersion}\</c> — the per-version local state root.</summary>
    public static string LocalBaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TurboSuite", RevitVersion ?? string.Empty);

    private static string? _serverPath;

    public static string ServerPath => _serverPath ??= LoadServerPath();

    private static string LoadServerPath()
    {
        try
        {
            var configPath = Path.Combine(LocalBaseDir, "config.json");

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
