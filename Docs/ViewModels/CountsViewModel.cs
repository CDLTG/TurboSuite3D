using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Docs.ViewModels;

public class CountsViewModel : ViewModelBase
{
    private readonly DocsViewModel _parent;
    private List<CountsFixtureModel> _fixtures = [];
    private double _progress;
    private string _statusText = string.Empty;
    private bool _isGenerating;
    private bool _isUpdateMode;
    private string _repDirectoryPath = string.Empty;
    private string _notifyEmail = string.Empty;
    private string _headerImagePath = string.Empty;
    private string _footerImagePath = string.Empty;

    public string ProjectName { get; }

    public int TypeCount => _fixtures.Count;
    public int TotalFixtureCount
    {
        get
        {
            int total = 0;
            foreach (var f in _fixtures) total += f.Count;
            return total;
        }
    }

    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool IsGenerating
    {
        get => _isGenerating;
        set
        {
            if (SetProperty(ref _isGenerating, value))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsUpdateMode
    {
        get => _isUpdateMode;
        set => SetProperty(ref _isUpdateMode, value);
    }

    public RelayCommand GenerateCommand { get; }
    public RelayCommand BrowseRepDirectoryCommand { get; }
    public RelayCommand BrowseHeaderImageCommand { get; }
    public RelayCommand BrowseFooterImageCommand { get; }

    public string RepDirectoryPath
    {
        get => _repDirectoryPath;
        set => SetProperty(ref _repDirectoryPath, value);
    }

    public string NotifyEmail
    {
        get => _notifyEmail;
        set => SetProperty(ref _notifyEmail, value);
    }

    public string HeaderImagePath
    {
        get => _headerImagePath;
        set => SetProperty(ref _headerImagePath, value);
    }

    public string FooterImagePath
    {
        get => _footerImagePath;
        set => SetProperty(ref _footerImagePath, value);
    }

    public CountsViewModel(string projectName, DocsViewModel parent)
    {
        _parent = parent;
        ProjectName = projectName;
        var settings = DocsSettingsService.Load();
        _repDirectoryPath = settings.RepDirectoryPath;
        _notifyEmail = settings.CountsNotifyEmail;
        _headerImagePath = settings.CountsHeaderImagePath;
        _footerImagePath = settings.CountsFooterImagePath;
        GenerateCommand = new RelayCommand(ExecuteGenerate, () => !IsGenerating && _fixtures.Count > 0);
        BrowseRepDirectoryCommand = new RelayCommand(ExecuteBrowseRepDirectory);
        BrowseHeaderImageCommand = new RelayCommand(() => BrowseImage(p => HeaderImagePath = p, HeaderImagePath));
        BrowseFooterImageCommand = new RelayCommand(() => BrowseImage(p => FooterImagePath = p, FooterImagePath));
    }

    public void LoadData(List<CountsFixtureModel> fixtures)
    {
        _fixtures = fixtures;
        OnPropertyChanged(nameof(TypeCount));
        OnPropertyChanged(nameof(TotalFixtureCount));
    }

    public void SaveSettings()
    {
        var settings = DocsSettingsService.Load();
        settings.RepDirectoryPath = _repDirectoryPath;
        settings.CountsNotifyEmail = _notifyEmail;
        settings.CountsHeaderImagePath = _headerImagePath;
        settings.CountsFooterImagePath = _footerImagePath;
        DocsSettingsService.Save(settings);
    }

    private static void BrowseImage(Action<string> setter, string current)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Image Files|*.png;*.jpg;*.jpeg",
            Title = "Select Image"
        };
        if (!string.IsNullOrWhiteSpace(current) && File.Exists(current))
            dlg.InitialDirectory = Path.GetDirectoryName(current);
        if (dlg.ShowDialog() == true)
            setter(dlg.FileName);
    }

    private void ExecuteBrowseRepDirectory()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Excel Files|*.xlsx;*.xlsm",
            Title = "Select Rep Directory Workbook"
        };
        if (!string.IsNullOrWhiteSpace(RepDirectoryPath) && File.Exists(RepDirectoryPath))
            dlg.InitialDirectory = Path.GetDirectoryName(RepDirectoryPath);
        if (dlg.ShowDialog() == true)
            RepDirectoryPath = dlg.FileName;
    }

    private async void ExecuteGenerate()
    {
        if (_fixtures.Count == 0) return;

        string? outputPath;

        if (IsUpdateMode)
        {
            var openDialog = new OpenFileDialog
            {
                Filter = "Excel Files|*.xlsx",
                Title = "Select Existing Counts Workbook"
            };
            if (openDialog.ShowDialog() != true) return;
            outputPath = openDialog.FileName;
        }
        else
        {
            var saveDialog = new SaveFileDialog
            {
                Filter = "Excel Files|*.xlsx",
                FileName = $"{ProjectName}_Counts.xlsx"
            };
            if (saveDialog.ShowDialog() != true) return;
            outputPath = saveDialog.FileName;
        }

        _parent.SaveSettings();
        IsGenerating = true;
        Progress = 0;

        try
        {
            bool updateMode = IsUpdateMode;
            var fixtures = _fixtures;
            string projName = ProjectName;
            string projLocation = _parent.ProjectLocation;
            string repDirPath = _repDirectoryPath;
            string headerImg = _headerImagePath;
            string footerImg = _footerImagePath;

            StatusText = updateMode ? "Updating workbook..." : "Generating workbook...";
            Progress = 30;

            await Task.Run(() =>
            {
                if (updateMode)
                    CountsWorkbookService.GenerateUpdate(fixtures, outputPath, repDirPath, headerImg, footerImg);
                else
                    CountsWorkbookService.GenerateNew(fixtures, projName, projLocation, outputPath, repDirPath, headerImg, footerImg);
            });

            Progress = 100;
            StatusText = $"Done. Saved to {Path.GetFileName(outputPath)}";

            // Open pre-filled email notification if a notify email is configured (Revit-side setting)
            string notifyEmail = _notifyEmail;
            if (!string.IsNullOrWhiteSpace(notifyEmail))
            {
                string subject = Uri.EscapeDataString($"Counts Updated - {projName}");
                string body = Uri.EscapeDataString($"The counts workbook for {projName} has been updated.\n\n{outputPath}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"mailto:{notifyEmail}?subject={subject}&body={body}",
                    UseShellExecute = true
                });
            }
        }
        catch (IOException)
        {
            StatusText = "Error: File is open in another application. Please close it and try again.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
        }
    }
}
