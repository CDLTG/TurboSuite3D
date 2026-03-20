using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace TurboSuite.Shared.Services;

public static class UpdateService
{
    private static readonly string LocalAppData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TurboSuite");

    private static readonly string VersionFilePath = Path.Combine(LocalAppData, "version.txt");
    private static readonly string StagingFolder = Path.Combine(LocalAppData, "Staging");
    private static readonly string StagingCompleteMarker = Path.Combine(StagingFolder, ".complete");
    private static readonly string UpdaterExePath = Path.Combine(LocalAppData, "TurboSuiteUpdater.exe");

    /// <summary>
    /// Checks whether a newer version is available on the server.
    /// Returns the new version string if an update is available, or null if not (or on any error).
    /// </summary>
    public static async Task<string?> CheckForUpdateAsync(CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                var serverVersionFile = Path.Combine(UpdateConstants.ServerPath, "version.txt");
                if (!File.Exists(serverVersionFile)) return null;

                var serverVersionText = File.ReadAllText(serverVersionFile).Trim();
                if (!Version.TryParse(serverVersionText, out var serverVersion)) return null;

                var localVersion = GetInstalledVersion();
                return serverVersion.CompareTo(localVersion) > 0 ? serverVersionText : null;
            }
            catch
            {
                return null;
            }
        }, ct);
    }

    /// <summary>
    /// Copies update files from the server to the local Staging folder.
    /// Writes a .complete marker last for crash safety.
    /// </summary>
    public static void StageUpdate()
    {
        if (Directory.Exists(StagingFolder))
            Directory.Delete(StagingFolder, true);

        Directory.CreateDirectory(StagingFolder);

        var serverPath = UpdateConstants.ServerPath;

        foreach (var sourceFile in Directory.GetFiles(serverPath))
        {
            var fileName = Path.GetFileName(sourceFile);
            var destFile = Path.Combine(StagingFolder, fileName);
            File.Copy(sourceFile, destFile, overwrite: true);
        }

        // Write marker last — if we crash before this, the staging is treated as incomplete
        File.WriteAllText(StagingCompleteMarker, "ok");
    }

    /// <summary>
    /// Returns true if a fully staged update is ready to apply.
    /// </summary>
    public static bool HasStagedUpdate()
    {
        return File.Exists(StagingCompleteMarker);
    }

    /// <summary>
    /// Returns the version string of the staged update, or null if unavailable.
    /// </summary>
    public static string? GetStagedVersion()
    {
        try
        {
            var stagedVersionFile = Path.Combine(StagingFolder, "version.txt");
            if (!File.Exists(stagedVersionFile)) return null;
            return File.ReadAllText(stagedVersionFile).Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Launches TurboSuiteUpdater.exe as a detached process to apply the staged update
    /// after Revit exits.
    /// </summary>
    public static void LaunchUpdater()
    {
        try
        {
            if (!File.Exists(UpdaterExePath)) return;

            var revitAddinsFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Autodesk\Revit\Addins\2025\TurboSuite");

            var startInfo = new ProcessStartInfo
            {
                FileName = UpdaterExePath,
                Arguments = $"--source \"{StagingFolder}\" --dest \"{revitAddinsFolder}\" --versionfile \"{VersionFilePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(startInfo);
        }
        catch
        {
            // Failed to launch updater — update will be attempted next time
        }
    }

    /// <summary>
    /// Gets the locally installed version. Falls back to assembly version if no version file exists.
    /// </summary>
    public static Version GetInstalledVersion()
    {
        try
        {
            if (File.Exists(VersionFilePath))
            {
                var text = File.ReadAllText(VersionFilePath).Trim();
                if (Version.TryParse(text, out var version))
                    return version;
            }
        }
        catch
        {
            // Fall through to assembly version
        }

        // First run or corrupt file — use assembly version as baseline and persist it
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        WriteInstalledVersion(assemblyVersion.ToString(3));
        return assemblyVersion;
    }

    /// <summary>
    /// Writes the installed version to the local version file.
    /// </summary>
    public static void WriteInstalledVersion(string version)
    {
        try
        {
            if (!Directory.Exists(LocalAppData))
                Directory.CreateDirectory(LocalAppData);

            File.WriteAllText(VersionFilePath, version);
        }
        catch
        {
            // Best-effort
        }
    }
}
