using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
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

    private int _progressValue;
    private string _statusText = "Ready to install.";
    private string _resultText = "";
    private Brush _resultColor = Brushes.Green;
    private bool _isInstalling;
    private bool _isComplete;

    public InstallerViewModel()
    {
        _sourceDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        var versionFile = Path.Combine(_sourceDir, "version.txt");
        var version = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "unknown";
        VersionText = $"TurboSuite v{version}";
        SourcePathText = $"Source: {_sourceDir}";

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
        _isInstalling = true;
        CommandManager.InvalidateRequerySuggested();

        try
        {
            // Step 1: Validate source files
            StatusText = "Validating source files...";
            ProgressValue = 5;

            foreach (var file in RequiredFiles)
            {
                if (!File.Exists(Path.Combine(_sourceDir, file)))
                {
                    Fail($"Missing required file: {file}");
                    return;
                }
            }

            ProgressValue = 10;

            // Step 2: Create target directories
            StatusText = "Creating directories...";

            var revitAddinsFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Autodesk\Revit\Addins\2025");
            var turboSuiteAddinsFolder = Path.Combine(revitAddinsFolder, "TurboSuite");
            var localAppDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TurboSuite");

            Directory.CreateDirectory(turboSuiteAddinsFolder);
            Directory.CreateDirectory(localAppDataFolder);

            ProgressValue = 20;

            // Step 3: Copy .addin manifest
            StatusText = "Copying add-in manifest...";
            await CopyFileAsync(
                Path.Combine(_sourceDir, "TurboSuite.addin"),
                Path.Combine(revitAddinsFolder, "TurboSuite.addin"));

            ProgressValue = 30;

            // Step 4: Copy DLLs and PDBs
            StatusText = "Copying add-in files...";
            var filesToCopy = Directory.GetFiles(_sourceDir);
            var copyCount = 0;

            foreach (var sourceFile in filesToCopy)
            {
                var fileName = Path.GetFileName(sourceFile);

                // Skip installer files and non-relevant files
                if (fileName.StartsWith("TurboSuiteInstaller", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (fileName.Equals("TurboSuiteUpdater.exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (fileName.Equals("TurboSuite.addin", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (fileName.Equals("version.txt", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (fileName.Equals("config.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                var ext = Path.GetExtension(fileName).ToLowerInvariant();
                if (ext is not (".dll" or ".pdb"))
                    continue;

                await CopyFileAsync(sourceFile, Path.Combine(turboSuiteAddinsFolder, fileName));
                copyCount++;
            }

            StatusText = $"Copied {copyCount} files...";
            ProgressValue = 60;

            // Step 5: Copy updater
            StatusText = "Copying updater...";
            await CopyFileAsync(
                Path.Combine(_sourceDir, "TurboSuiteUpdater.exe"),
                Path.Combine(localAppDataFolder, "TurboSuiteUpdater.exe"));

            ProgressValue = 70;

            // Step 6: Write config.json with server path
            StatusText = "Writing configuration...";
            var config = new { ServerPath = _sourceDir };
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(localAppDataFolder, "config.json"), json);

            ProgressValue = 80;

            // Step 7: Write version.txt
            StatusText = "Writing version info...";
            await CopyFileAsync(
                Path.Combine(_sourceDir, "version.txt"),
                Path.Combine(localAppDataFolder, "version.txt"));

            ProgressValue = 100;

            // Done
            StatusText = "Installation complete.";
            ResultColor = Brushes.Green;
            ResultText = "TurboSuite has been installed. Launch Revit to get started.";
        }
        catch (Exception ex)
        {
            Fail($"Installation failed: {ex.Message}");
            return;
        }

        _isComplete = true;
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task RunUninstallAsync()
    {
        var result = MessageBox.Show(
            "This will remove all TurboSuite files. Continue?",
            "Confirm Uninstall",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        _isInstalling = true;
        CommandManager.InvalidateRequerySuggested();

        try
        {
            var revitAddinsFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Autodesk\Revit\Addins\2025");
            var turboSuiteAddinsFolder = Path.Combine(revitAddinsFolder, "TurboSuite");
            var addinManifest = Path.Combine(revitAddinsFolder, "TurboSuite.addin");
            var localAppDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TurboSuite");

            // Step 1: Remove .addin manifest
            StatusText = "Removing add-in manifest...";
            ProgressValue = 20;
            if (File.Exists(addinManifest))
                File.Delete(addinManifest);

            // Step 2: Remove add-in folder
            StatusText = "Removing add-in files...";
            ProgressValue = 50;
            if (Directory.Exists(turboSuiteAddinsFolder))
                Directory.Delete(turboSuiteAddinsFolder, true);

            // Step 3: Remove local app data folder (config, updater, staging, version)
            StatusText = "Removing local data...";
            ProgressValue = 80;
            if (Directory.Exists(localAppDataFolder))
                Directory.Delete(localAppDataFolder, true);

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
