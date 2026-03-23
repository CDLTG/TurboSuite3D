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
    private DateTime _headerDate = DateTime.Now;
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

    public DateTime HeaderDate
    {
        get => _headerDate;
        set => SetProperty(ref _headerDate, value);
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
    public RelayCommand<FixtureSpecModel> BrowseLocalPdfCommand { get; }
    public RelayCommand<FixtureSpecModel> ClearLocalPdfCommand { get; }
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

        // Restore persisted local PDF paths
        foreach (var fixture in Fixtures)
        {
            if (settings.LocalPdfPaths.TryGetValue(fixture.TypeMark, out var path) && File.Exists(path))
                fixture.LocalPdfPath = path;
        }

        BrowseLogoCommand = new RelayCommand(ExecuteBrowseLogo);
        BrowseLocalPdfCommand = new RelayCommand<FixtureSpecModel>(ExecuteBrowseLocalPdf);
        ClearLocalPdfCommand = new RelayCommand<FixtureSpecModel>(f => f.LocalPdfPath = string.Empty);
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true));
        DeselectAllCommand = new RelayCommand(() => SetAllSelected(false));
        GenerateCommand = new RelayCommand(ExecuteGenerate, () => !IsGenerating && Fixtures.Any(f => f.IsSelected));
    }

    public void SaveSettings()
    {
        var localPdfPaths = Fixtures
            .Where(f => f.HasLocalPdf)
            .ToDictionary(f => f.TypeMark, f => f.LocalPdfPath);

        CutsSettingsService.Save(new CutsSettings
        {
            LogoFilePath = LogoFilePath,
            CompanyAddress = CompanyAddress,
            CompanyPhone = CompanyPhone,
            CompanyWebsite = CompanyWebsite,
            LocalPdfPaths = localPdfPaths
        });
    }

    private void ExecuteBrowseLogo()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.pdf",
            Title = "Select Company Logo"
        };
        if (dialog.ShowDialog() == true)
            LogoFilePath = dialog.FileName;
    }

    private void ExecuteBrowseLocalPdf(FixtureSpecModel fixture)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PDF Files|*.pdf",
            Title = $"Select Local PDF for {fixture.TypeMark}"
        };
        if (dialog.ShowDialog() == true)
            fixture.LocalPdfPath = dialog.FileName;
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

        var results = new List<(string typeMark, byte[]? pdfData, string catalogNumber)>();
        var errors = new List<string>();

        try
        {
            // Load/download phase (0–80%)
            for (int i = 0; i < selected.Count; i++)
            {
                var fixture = selected[i];
                Progress = (double)i / selected.Count * 80.0;

                byte[]? data;
                if (fixture.HasLocalPdf && File.Exists(fixture.LocalPdfPath))
                {
                    StatusText = $"Loading {i + 1} of {selected.Count}: {fixture.TypeMark}...";
                    data = await DownloadService.ReadLocalPdfAsync(fixture.LocalPdfPath);
                    if (data == null) errors.Add(fixture.TypeMark);
                }
                else if (!string.IsNullOrWhiteSpace(fixture.DataSheetUrl))
                {
                    StatusText = $"Downloading {i + 1} of {selected.Count}: {fixture.TypeMark}...";
                    data = await DownloadService.DownloadPdfAsync(fixture.DataSheetUrl, CancellationToken.None);
                    if (data == null) errors.Add(fixture.TypeMark);
                }
                else
                {
                    data = null;
                }

                results.Add((fixture.TypeMark, data, fixture.CatalogNumber));
            }

            // Merge phase (80–100%)
            StatusText = "Merging PDFs...";
            Progress = 85;

            var settings = new CutsSettings
            {
                LogoFilePath = LogoFilePath,
                CompanyAddress = CompanyAddress,
                CompanyPhone = CompanyPhone,
                CompanyWebsite = CompanyWebsite,
                HeaderDate = HeaderDate.ToString("MMM dd, yyyy")
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
