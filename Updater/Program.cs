using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

// TurboSuiteUpdater — waits for all Revit instances to exit, then copies staged files to the add-in folder.
// Args: --source <staging path> --dest <addins path> --versionfile <local version.txt path>

var source = GetArg(args, "--source");
var dest = GetArg(args, "--dest");
var versionFile = GetArg(args, "--versionfile");

if (source is null || dest is null || versionFile is null)
{
    Console.Error.WriteLine("Usage: TurboSuiteUpdater --source <path> --dest <path> --versionfile <path>");
    return 1;
}

if (!Directory.Exists(source))
{
    Console.Error.WriteLine($"Staging folder not found: {source}");
    return 1;
}

// Wait for ALL Revit processes to exit (handles multiple instances)
var timeout = TimeSpan.FromSeconds(120);
var sw = Stopwatch.StartNew();

while (sw.Elapsed < timeout)
{
    var revitProcesses = Process.GetProcessesByName("Revit");
    if (revitProcesses.Length == 0) break;

    foreach (var p in revitProcesses) p.Dispose();
    Thread.Sleep(1000);
}

// If Revit is still running after timeout, abort — don't risk copying over locked files
if (Process.GetProcessesByName("Revit").Length > 0)
{
    Console.Error.WriteLine("Timeout waiting for Revit to exit. Update aborted.");
    return 1;
}

try
{
    if (!Directory.Exists(dest))
        Directory.CreateDirectory(dest);

    // Copy all files except the .complete marker
    foreach (var sourceFile in Directory.GetFiles(source))
    {
        var fileName = Path.GetFileName(sourceFile);
        if (fileName == ".complete") continue;

        var destFile = Path.Combine(dest, fileName);
        File.Copy(sourceFile, destFile, overwrite: true);
    }

    // Update the local version file from the staged version.txt
    var stagedVersion = Path.Combine(source, "version.txt");
    if (File.Exists(stagedVersion))
    {
        var versionDir = Path.GetDirectoryName(versionFile);
        if (versionDir is not null && !Directory.Exists(versionDir))
            Directory.CreateDirectory(versionDir);

        File.Copy(stagedVersion, versionFile, overwrite: true);
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
