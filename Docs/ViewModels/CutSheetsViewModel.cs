using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Docs.ViewModels;

public class CutSheetsViewModel : ViewModelBase
{
    private readonly DocsViewModel _parent;
    private double _progress;
    private string _statusText = string.Empty;
    private bool _isGenerating;

    public string ProjectName { get; }
    public ObservableCollection<FixtureSpecModel> Fixtures { get; }

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

    public RelayCommand<FixtureSpecModel> BrowseLocalPdfCommand { get; }
    public RelayCommand<FixtureSpecModel> ClearLocalPdfCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand DeselectAllCommand { get; }
    public RelayCommand GenerateCommand { get; }

    public CutSheetsViewModel(List<FixtureSpecModel> fixtures, string projectName, DocsViewModel parent)
    {
        _parent = parent;
        ProjectName = projectName;
        Fixtures = new ObservableCollection<FixtureSpecModel>(fixtures);

        var settings = DocsSettingsService.Load();

        foreach (var fixture in Fixtures)
        {
            if (settings.LocalPdfPaths.TryGetValue(fixture.TypeMark, out var path) && File.Exists(path))
                fixture.LocalPdfPath = path;

            if (settings.SelectedTypeMarks.Count > 0)
                fixture.IsSelected = settings.SelectedTypeMarks.Contains(fixture.TypeMark);
        }

        BrowseLocalPdfCommand = new RelayCommand<FixtureSpecModel>(ExecuteBrowseLocalPdf);
        ClearLocalPdfCommand = new RelayCommand<FixtureSpecModel>(f => f.LocalPdfPath = string.Empty);
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true));
        DeselectAllCommand = new RelayCommand(() => SetAllSelected(false));
        GenerateCommand = new RelayCommand(ExecuteGenerate, () => !IsGenerating && Fixtures.Any(f => f.IsSelected));
    }

    public void SaveSettings()
    {
        var settings = DocsSettingsService.Load();
        settings.LocalPdfPaths = Fixtures
            .Where(f => f.HasLocalPdf)
            .ToDictionary(f => f.TypeMark, f => f.LocalPdfPath);
        settings.SelectedTypeMarks = Fixtures
            .Where(f => f.IsSelected)
            .Select(f => f.TypeMark)
            .ToList();
        DocsSettingsService.Save(settings);
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

        _parent.SaveSettings();
        IsGenerating = true;
        Progress = 0;

        var results = new List<(string typeMark, byte[]? pdfData, string catalogNumber)>();
        var errors = new List<string>();

        try
        {
            // Load/download phase (0-80%)
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

            // Merge phase (80-100%)
            StatusText = "Merging PDFs...";
            Progress = 85;

            var settings = new DocsSettings
            {
                LogoFilePath = _parent.LogoFilePath,
                CompanyAddress = _parent.CompanyAddress,
                CompanyPhone = _parent.CompanyPhone,
                CompanyEmail = _parent.CompanyEmail,
                CompanyWebsite = _parent.CompanyWebsite,
                HeaderDate = _parent.HeaderDate.ToString("MMM dd, yyyy")
            };

            string outputPath = saveDialog.FileName;
            await Task.Run(() => CutSheetPdfService.MergeAndStamp(results, settings, ProjectName, outputPath));

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
