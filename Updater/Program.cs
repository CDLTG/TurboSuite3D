using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

// TurboSuiteUpdater — waits for all Revit instances to exit, then copies staged files to the add-in folder.
// Args: --source <staging path> --dest <addins path> --versionfile <local version.txt path>

// Ensure only one updater instance runs at a time
using var mutex = new Mutex(true, "TurboSuiteUpdater_SingleInstance", out var isNew);
if (!isNew) return 0;

var source = GetArg(args, "--source");
var dest = GetArg(args, "--dest");
var versionFile = GetArg(args, "--versionfile");
var pidArg = GetArg(args, "--pid");

if (source is null || dest is null || versionFile is null)
{
    Console.Error.WriteLine("Usage: TurboSuiteUpdater --source <path> --dest <path> --versionfile <path> [--pid <revit-pid>]");
    return 1;
}

if (!Directory.Exists(source))
{
    Console.Error.WriteLine($"Staging folder not found: {source}");
    return 1;
}

// Wait for the specific Revit process that launched us, or fall back to waiting for all Revit processes
var timeout = TimeSpan.FromSeconds(120);
var sw = Stopwatch.StartNew();

if (pidArg is not null && int.TryParse(pidArg, out var pid))
{
    try
    {
        var revit = Process.GetProcessById(pid);
        revit.WaitForExit((int)timeout.TotalMilliseconds);
        revit.Dispose();
    }
    catch (ArgumentException)
    {
        // Process already exited
    }
}
else
{
    // Legacy fallback: wait for all Revit processes
    while (sw.Elapsed < timeout)
    {
        var revitProcesses = Process.GetProcessesByName("Revit");
        if (revitProcesses.Length == 0) break;

        foreach (var p in revitProcesses) p.Dispose();
        Thread.Sleep(1000);
    }

    if (Process.GetProcessesByName("Revit").Length > 0)
    {
        Console.Error.WriteLine("Timeout waiting for Revit to exit. Update aborted.");
        return 1;
    }
}

try
{
    if (!Directory.Exists(dest))
        Directory.CreateDirectory(dest);

    // Files that belong in LocalAppData, not the addins folder
    var localAppDataFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "TurboSuiteUpdater.exe", "TurboSuiteUpdater.dll", "TurboSuiteUpdater.runtimeconfig.json",
        "version.txt"
    };
    var skipFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".complete" };

    var versionDir = Path.GetDirectoryName(versionFile);
    if (versionDir is not null && !Directory.Exists(versionDir))
        Directory.CreateDirectory(versionDir);

    foreach (var sourceFile in Directory.GetFiles(source))
    {
        var fileName = Path.GetFileName(sourceFile);
        if (skipFiles.Contains(fileName)) continue;

        // Skip installer files — they don't belong in the addins folder
        if (fileName.StartsWith("TurboSuiteInstaller", StringComparison.OrdinalIgnoreCase)) continue;

        if (localAppDataFiles.Contains(fileName))
        {
            // Update files in LocalAppData (updater self-update + version.txt)
            var localDest = Path.Combine(versionDir!, fileName);
            try { File.Copy(sourceFile, localDest, overwrite: true); }
            catch { /* Updater exe/dll may be locked by this process — updated next time */ }
        }
        else
        {
            // Copy add-in files (DLLs, PDBs, .addin) to the Revit addins folder
            var destFile = Path.Combine(dest, fileName);
            File.Copy(sourceFile, destFile, overwrite: true);
        }
    }

    // Also copy .addin manifest to the parent directory (Revit discovers it there)
    var stagedAddin = Path.Combine(source, "TurboSuite.addin");
    if (File.Exists(stagedAddin))
    {
        var addinsParent = Path.GetDirectoryName(dest);
        if (addinsParent is not null)
            File.Copy(stagedAddin, Path.Combine(addinsParent, "TurboSuite.addin"), overwrite: true);
    }

    // Clean up staging folder
    Directory.Delete(source, true);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Update failed: {ex.Message}");
    return 1;
}

return 0;

static string? GetArg(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
