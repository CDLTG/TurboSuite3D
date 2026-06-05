using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TurboSuiteInstaller;

public class InstallerViewModel : INotifyPropertyChanged
{
    private static readonly string[] RequiredFiles =
        ["TurboSuite.dll", "TurboSuite.addin", "TurboSuiteUpdater.exe", "version.txt"];

    private readonly string _sourceDir;
    private readonly List<Channel> _channels;

    private int _progressValue;
    private string _statusText = "Ready to install.";
    private string _resultText = "";
    private Brush _resultColor = Brushes.Green;
    private bool _isInstalling;
    private bool _isComplete;

    public InstallerViewModel()
    {
        _sourceDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        // Each Revit version ships as a sibling subfolder of the installer (\2024\, \2025\, …).
        // Discover them by shape rather than a hardcoded list so a future \2027\ channel needs
        // no installer change — just publish the subfolder.
        _channels = DiscoverChannels(_sourceDir);

        SourcePathText = $"Source: {_sourceDir}";
        VersionText = _channels.Count == 0
            ? "TurboSuite — no version channels found"
            : "TurboSuite — " + string.Join(", ", _channels.Select(c => $"{c.Year} (v{c.VersionString})"));

        InstallCommand = new SimpleCommand(async () => await RunInstallAsync(), () => !_isInstalling && !_isComplete);
        UninstallCommand = new SimpleCommand(async () => await RunUninstallAsync(), () => !_isInstalling && !_isComplete);
        CloseCommand = new SimpleCommand(() => Application.Current.Shutdown());
    }

    public string VersionText { get; }
    public string SourcePathText { get; }

    public int ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string ResultText
    {
        get => _resultText;
        set => SetProperty(ref _resultText, value);
    }

    public Brush ResultColor
    {
        get => _resultColor;
        set => SetProperty(ref _resultColor, value);
    }

    public ICommand InstallCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand CloseCommand { get; }

    private async Task RunInstallAsync()
    {
        if (_channels.Count == 0)
        {
            Fail("No TurboSuite version channels (e.g. \\2024\\, \\2025\\) were found next to the installer.");
            return;
        }

        // Install each channel whose matching Revit version is present. If none match, offer to
        // install all available channels ahead of Revit (the add-in loads once that Revit is installed).
        var targets = _channels.Where(c => c.RevitInstalled).ToList();
        if (targets.Count == 0)
        {
            var available = string.Join(", ", _channels.Select(c => c.Year));
            var proceed = MessageBox.Show(
                $"No matching Revit installation was found for the available channel(s): {available}.\n\n" +
                "You can install ahead of Revit — each add-in will load once its matching Revit " +
                "version is present.\n\n" +
                "Install all available channels anyway?",
                "No matching Revit detected",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.Yes) return;
            targets = _channels;
        }

        _isInstalling = true;
        CommandManager.InvalidateRequerySuggested();

        try
        {
            var done = 0;
            foreach (var channel in targets)
            {
                StatusText = $"Installing Revit {channel.Year} channel (v{channel.VersionString})...";
                await InstallChannelAsync(channel);
                done++;
                ProgressValue = (int)(100.0 * done / targets.Count);
            }

            StatusText = "Installation complete.";
            ResultColor = Brushes.Green;
            ResultText = $"Installed {targets.Count} channel(s): {string.Join(", ", targets.Select(t => t.Year))}. " +
                         "Launch Revit to get started.";
        }
        catch (Exception ex)
        {
            Fail($"Installation failed: {ex.Message}");
            return;
        }

        _isComplete = true;
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task InstallChannelAsync(Channel channel)
    {
        var src = channel.SourceDir;

        foreach (var file in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(src, file)))
                throw new FileNotFoundException($"Missing required file in the {channel.Year} channel: {file}");
        }

        var revitAddinsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", channel.Year);
        var turboSuiteAddinsFolder = Path.Combine(revitAddinsFolder, "TurboSuite");
        var localAppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TurboSuite", channel.Year);

        Directory.CreateDirectory(turboSuiteAddinsFolder);
        Directory.CreateDirectory(localAppDataFolder);

        // .addin manifest lives in the version's Addins root
        await CopyFileAsync(
            Path.Combine(src, "TurboSuite.addin"),
            Path.Combine(revitAddinsFolder, "TurboSuite.addin"));

        // DLLs/PDBs → Addins\{ver}\TurboSuite\ (updater files are routed to LocalAppData below)
        foreach (var sourceFile in Directory.GetFiles(src))
        {
            var fileName = Path.GetFileName(sourceFile);
            if (fileName.StartsWith("TurboSuiteInstaller", StringComparison.OrdinalIgnoreCase)) continue;
            if (fileName.StartsWith("TurboSuiteUpdater", StringComparison.OrdinalIgnoreCase)) continue;

            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext is not (".dll" or ".pdb")) continue;

            await CopyFileAsync(sourceFile, Path.Combine(turboSuiteAddinsFolder, fileName));
        }

        // Updater (net48: just the exe; net8: exe + dll + runtimeconfig.json) → LocalAppData\{ver}\
        foreach (var updaterFile in Directory.GetFiles(src, "TurboSuiteUpdater.*"))
            await CopyFileAsync(updaterFile, Path.Combine(localAppDataFolder, Path.GetFileName(updaterFile)));

        // config.json — ServerPath points at this channel's share subfolder so UpdateService
        // scans the right per-version folder for updates.
        var config = new { ServerPath = src };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(localAppDataFolder, "config.json"), json);

        // version.txt
        await CopyFileAsync(
            Path.Combine(src, "version.txt"),
            Path.Combine(localAppDataFolder, "version.txt"));
    }

    private async Task RunUninstallAsync()
    {
        var result = MessageBox.Show(
            "This will remove all TurboSuite files for every installed Revit version. Continue?",
            "Confirm Uninstall",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        if (System.Diagnostics.Process.GetProcessesByName("Revit").Length > 0)
        {
            Fail("Please close Revit before uninstalling.");
            return;
        }

        _isInstalling = true;
        CommandManager.InvalidateRequerySuggested();

        try
        {
            // Remove TurboSuite from every Revit addins version folder.
            StatusText = "Removing add-in files...";
            ProgressValue = 30;
            var addinsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Addins");
            if (Directory.Exists(addinsRoot))
            {
                foreach (var versionDir in Directory.GetDirectories(addinsRoot))
                {
                    var manifest = Path.Combine(versionDir, "TurboSuite.addin");
                    var turboSuiteFolder = Path.Combine(versionDir, "TurboSuite");
                    if (File.Exists(manifest)) File.Delete(manifest);
                    if (Directory.Exists(turboSuiteFolder)) Directory.Delete(turboSuiteFolder, true);
                }
            }

            // Remove all per-version local data (config, updater, staging, version).
            StatusText = "Removing local data...";
            ProgressValue = 80;
            var localRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TurboSuite");
            if (Directory.Exists(localRoot)) Directory.Delete(localRoot, true);

            ProgressValue = 100;
            StatusText = "Uninstall complete.";
            ResultColor = Brushes.Green;
            ResultText = "TurboSuite has been removed.";
        }
        catch (Exception ex)
        {
            Fail($"Uninstall failed: {ex.Message}");
            return;
        }

        _isComplete = true;
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Discovers per-version channel subfolders (named like a Revit year, e.g. "2025") that hold a
    /// built TurboSuite.dll and version.txt.
    /// </summary>
    private static List<Channel> DiscoverChannels(string root)
    {
        var channels = new List<Channel>();
        if (!Directory.Exists(root)) return channels;

        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (!Regex.IsMatch(name, @"^20\d{2}$")) continue;

            var versionFile = Path.Combine(dir, "version.txt");
            if (!File.Exists(Path.Combine(dir, "TurboSuite.dll")) || !File.Exists(versionFile)) continue;

            string versionString;
            try { versionString = File.ReadAllText(versionFile).Trim(); }
            catch { versionString = "unknown"; }

            channels.Add(new Channel(name, versionString, dir));
        }

        return channels.OrderBy(c => c.Year, StringComparer.Ordinal).ToList();
    }

    private void Fail(string message)
    {
        StatusText = "Installation failed.";
        ResultColor = new SolidColorBrush(Color.FromRgb(200, 40, 40));
        ResultText = message;
        _isInstalling = false;
        CommandManager.InvalidateRequerySuggested();
    }

    private static async Task CopyFileAsync(string source, string destination)
    {
        await using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        await using var destStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        await sourceStream.CopyToAsync(destStream);
    }

    /// <summary>A per-Revit-version deployment channel on the share.</summary>
    private sealed record Channel(string Year, string VersionString, string SourceDir)
    {
        /// <summary>True if the matching Revit version is installed in the default location.</summary>
        public bool RevitInstalled => File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Autodesk", $"Revit {Year}", "Revit.exe"));
    }

    // INotifyPropertyChanged
    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public class SimpleCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public SimpleCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public SimpleCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
    {
        _execute = () => _ = executeAsync();
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
