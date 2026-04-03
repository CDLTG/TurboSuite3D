using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Docs.ViewModels;

public class LoadsViewModel : ViewModelBase
{
    private readonly DocsViewModel _parent;
    private string _selectedSortColumn = "CircuitNumber";
    private bool _sortDescending;
    private double _progress;
    private string _statusText = string.Empty;
    private bool _isGenerating;

    public string ProjectName { get; }
    public ObservableCollection<LoadsCircuitModel> Circuits { get; } = new();

    public string SelectedSortColumn
    {
        get => _selectedSortColumn;
        set => SetProperty(ref _selectedSortColumn, value);
    }

    public bool SortDescending
    {
        get => _sortDescending;
        set => SetProperty(ref _sortDescending, value);
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

    public RelayCommand GenerateCommand { get; }

    public LoadsViewModel(string projectName, DocsViewModel parent)
    {
        _parent = parent;
        ProjectName = projectName;

        GenerateCommand = new RelayCommand(ExecuteGenerate, () => !IsGenerating && Circuits.Count > 0);
    }

    public void LoadCircuits(List<LoadsCircuitModel> circuits)
    {
        Circuits.Clear();
        foreach (var c in circuits)
            Circuits.Add(c);

        var settings = DocsSettingsService.Load();
        if (!string.IsNullOrWhiteSpace(settings.LoadsSelectedSortColumn))
            _selectedSortColumn = settings.LoadsSelectedSortColumn;
    }

    public void SaveSettings()
    {
        var settings = DocsSettingsService.Load();
        settings.LoadsSelectedSortColumn = SelectedSortColumn;
        DocsSettingsService.Save(settings);
    }

    private List<LoadsCircuitModel> GetSortedCircuits()
    {
        IEnumerable<LoadsCircuitModel> ordered = SelectedSortColumn switch
        {
            "LoadName" when SortDescending => Circuits.OrderByDescending(c => c.LoadName, StringComparer.OrdinalIgnoreCase),
            "LoadName" => Circuits.OrderBy(c => c.LoadName, StringComparer.OrdinalIgnoreCase),
            "TotalWatts" when SortDescending => Circuits.OrderByDescending(c => c.ApparentLoadVA),
            "TotalWatts" => Circuits.OrderBy(c => c.ApparentLoadVA),
            _ when SortDescending => Circuits.OrderByDescending(c => c.CircuitNumber, StringComparer.OrdinalIgnoreCase),
            _ => Circuits.OrderBy(c => c.CircuitNumber, StringComparer.OrdinalIgnoreCase),
        };
        return ordered.ToList();
    }

    private async void ExecuteGenerate()
    {
        if (Circuits.Count == 0) return;

        var saveDialog = new SaveFileDialog
        {
            Filter = "PDF Files|*.pdf",
            FileName = $"{ProjectName} Load Schedule.pdf"
        };
        if (saveDialog.ShowDialog() != true) return;

        _parent.SaveSettings();
        IsGenerating = true;
        Progress = 0;

        try
        {
            StatusText = "Generating load schedule...";
            Progress = 50;

            var sorted = GetSortedCircuits();
            string outputPath = saveDialog.FileName;
            var settings = new DocsSettings
            {
                LogoFilePath = _parent.LogoFilePath,
                CompanyAddress = _parent.CompanyAddress,
                CompanyPhone = _parent.CompanyPhone,
                CompanyEmail = _parent.CompanyEmail,
                CompanyWebsite = _parent.CompanyWebsite,
            };
            await Task.Run(() => LoadsPdfService.Generate(sorted, ProjectName, outputPath, settings));

            Progress = 100;
            StatusText = $"Done. Saved to {Path.GetFileName(outputPath)}";
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
