using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using TurboSuite.Cuts.Models;
using TurboSuite.Cuts.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Cuts.ViewModels;

public class TurboCutsViewModel : ViewModelBase
{
    private string _logoFilePath = string.Empty;
    private string _companyAddress = string.Empty;
    private string _companyPhone = string.Empty;
    private string _companyWebsite = string.Empty;
    private double _progress;
    private string _statusText = string.Empty;
    private bool _isGenerating;

    public string ProjectName { get; }
    public ObservableCollection<FixtureSpecModel> Fixtures { get; }

    public string LogoFilePath
    {
        get => _logoFilePath;
        set => SetProperty(ref _logoFilePath, value);
    }

    public string CompanyAddress
    {
        get => _companyAddress;
        set => SetProperty(ref _companyAddress, value);
    }

    public string CompanyPhone
    {
        get => _companyPhone;
        set => SetProperty(ref _companyPhone, value);
    }

    public string CompanyWebsite
    {
        get => _companyWebsite;
        set => SetProperty(ref _companyWebsite, value);
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

    public RelayCommand BrowseLogoCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand DeselectAllCommand { get; }
    public RelayCommand GenerateCommand { get; }

    public TurboCutsViewModel(List<FixtureSpecModel> fixtures, string projectName)
    {
        ProjectName = projectName;
        Fixtures = new ObservableCollection<FixtureSpecModel>(fixtures);

        var settings = CutsSettingsService.Load();
        _logoFilePath = settings.LogoFilePath;
        _companyAddress = settings.CompanyAddress;
        _companyPhone = settings.CompanyPhone;
        _companyWebsite = settings.CompanyWebsite;

        BrowseLogoCommand = new RelayCommand(ExecuteBrowseLogo);
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true));
        DeselectAllCommand = new RelayCommand(() => SetAllSelected(false));
        GenerateCommand = new RelayCommand(ExecuteGenerate, () => !IsGenerating && Fixtures.Any(f => f.IsSelected));
    }

    public void SaveSettings()
    {
        CutsSettingsService.Save(new CutsSettings
        {
            LogoFilePath = LogoFilePath,
            CompanyAddress = CompanyAddress,
            CompanyPhone = CompanyPhone,
            CompanyWebsite = CompanyWebsite
        });
    }

    private void ExecuteBrowseLogo()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp",
            Title = "Select Company Logo"
        };
        if (dialog.ShowDialog() == true)
            LogoFilePath = dialog.FileName;
    }

    private void SetAllSelected(bool selected)
    {
        foreach (var fixture in Fixtures)
            fixture.IsSelected = selected;
    }

    private async void ExecuteGenerate()
    {
        var selected = Fixtures.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0) return;

        var saveDialog = new SaveFileDialog
        {
            Filter = "PDF Files|*.pdf",
            FileName = $"{ProjectName} Cut Sheets.pdf"
        };
        if (saveDialog.ShowDialog() != true) return;

        SaveSettings();
        IsGenerating = true;
        Progress = 0;

        var results = new List<(string typeMark, byte[] pdfData)>();
        var errors = new List<string>();

        try
        {
            // Download phase (0–80%)
            for (int i = 0; i < selected.Count; i++)
            {
                var fixture = selected[i];
                StatusText = $"Downloading {i + 1} of {selected.Count}: {fixture.TypeMark}...";
                Progress = (double)i / selected.Count * 80.0;

                var data = await DownloadService.DownloadPdfAsync(fixture.DataSheetUrl, CancellationToken.None);
                if (data != null)
                    results.Add((fixture.TypeMark, data));
                else
                    errors.Add(fixture.TypeMark);
            }

            if (results.Count == 0)
            {
                StatusText = "All downloads failed. No PDF generated.";
                IsGenerating = false;
                return;
            }

            // Merge phase (80–100%)
            StatusText = "Merging PDFs...";
            Progress = 85;

            var settings = new CutsSettings
            {
                LogoFilePath = LogoFilePath,
                CompanyAddress = CompanyAddress,
                CompanyPhone = CompanyPhone,
                CompanyWebsite = CompanyWebsite
            };

            string outputPath = saveDialog.FileName;
            await Task.Run(() => PdfService.MergeAndStamp(results, settings, ProjectName, outputPath));

            Progress = 100;
            StatusText = errors.Count > 0
                ? $"Done. {errors.Count} failed: {string.Join(", ", errors)}. Saved to {Path.GetFileName(outputPath)}"
                : $"Done. Saved to {Path.GetFileName(outputPath)}";
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
