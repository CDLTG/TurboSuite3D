using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace TurboSuite.Shared.Services;

/// <summary>Outcome of a single server update check.</summary>
public enum UpdateCheckStatus
{
    /// <summary>The server was reached and advertises a newer version (see <see cref="UpdateCheckResult.NewVersion"/>).</summary>
    UpdateAvailable,
    /// <summary>The server was reached and the local install is already current.</summary>
    UpToDate,
    /// <summary>
    /// The server could not be reached in time (cold SMB share still connecting, IO error, or no
    /// configured path). Transient — worth retrying on a later attempt rather than giving up.
    /// </summary>
    Unavailable
}

/// <summary>Result of <see cref="UpdateService.CheckForUpdateAsync"/>.</summary>
public readonly record struct UpdateCheckResult(UpdateCheckStatus Status, string? NewVersion);

/// <summary>
/// Checks the configured server share for a newer <c>version.txt</c> on Revit launch, stages new files
/// to <c>%LOCALAPPDATA%\TurboSuite\Staging\</c>, and prompts the user. Accepted updates are applied
/// by <c>TurboSuiteUpdater.exe</c> after Revit exits.
/// </summary>
public static class UpdateService
{
    // Per-version local state root (%LOCALAPPDATA%\TurboSuite\{RevitVersion}\). Computed
    // properties, not static-readonly fields, because the version is only known at runtime
    // (set in OnStartup) — type-load would capture it before UpdateConstants.RevitVersion is set.
    private static string LocalAppData => UpdateConstants.LocalBaseDir;
    private static string VersionFilePath => Path.Combine(LocalAppData, "version.txt");
    private static string StagingFolder => Path.Combine(LocalAppData, "Staging");
    private static string StagingCompleteMarker => Path.Combine(StagingFolder, ".complete");
    private static string UpdaterExePath => Path.Combine(LocalAppData, "TurboSuiteUpdater.exe");

    /// <summary>
    /// Checks whether a newer version is available on the server. Returns a status that distinguishes
    /// "newer version found", "already current", and "couldn't reach the server in time" — the last is
    /// transient (a cold share still connecting) and the caller is expected to retry rather than give up.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct)
    {
        // Why: File.Exists / File.ReadAllText on a half-responsive SMB share can block
        // for the full Windows SMB timeout (~30-60s) regardless of CancellationToken.
        // Race the work against a Delay so the caller returns predictably on timeout.
        // The inner Task may still be alive in the background, but we stop waiting on it.
        var workTask = Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrEmpty(UpdateConstants.ServerPath))
                    return new UpdateCheckResult(UpdateCheckStatus.Unavailable, null);

                var serverVersionFile = Path.Combine(UpdateConstants.ServerPath, "version.txt");
                if (!File.Exists(serverVersionFile))
                    return new UpdateCheckResult(UpdateCheckStatus.Unavailable, null);

                var serverVersionText = File.ReadAllText(serverVersionFile).Trim();
                if (!Version.TryParse(serverVersionText, out var serverVersion))
                    return new UpdateCheckResult(UpdateCheckStatus.Unavailable, null);

                var localVersion = GetInstalledVersion();
                return serverVersion.CompareTo(localVersion) > 0
                    ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, serverVersionText)
                    : new UpdateCheckResult(UpdateCheckStatus.UpToDate, null);
            }
            catch
            {
                return new UpdateCheckResult(UpdateCheckStatus.Unavailable, null);
            }
        });

        var timeoutTask = Task.Delay(Timeout.Infinite, ct);
        var winner = await Task.WhenAny(workTask, timeoutTask);
        return winner == workTask
            ? await workTask
            : new UpdateCheckResult(UpdateCheckStatus.Unavailable, null);
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
        if (string.IsNullOrEmpty(serverPath)) return;

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
                "Autodesk", "Revit", "Addins", UpdateConstants.RevitVersion ?? string.Empty, "TurboSuite");

            var revitPid = Process.GetCurrentProcess().Id;

            var startInfo = new ProcessStartInfo
            {
                FileName = UpdaterExePath,
                Arguments = $"--source \"{StagingFolder}\" --dest \"{revitAddinsFolder}\" --versionfile \"{VersionFilePath}\" --pid {revitPid}",
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
