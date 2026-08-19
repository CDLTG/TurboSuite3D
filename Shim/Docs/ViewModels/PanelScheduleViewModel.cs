using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Docs.ViewModels;

public class PanelScheduleViewModel : ViewModelBase
{
    private readonly DocsViewModel _parent;
    private PanelScheduleData? _data;
    private string _summaryText = "No panel data available. Run TurboZones first.";
    private double _progress;
    private string _statusText = string.Empty;
    private bool _isGenerating;

    public string ProjectName { get; }

    public PanelScheduleData? Data
    {
        get => _data;
        private set => SetProperty(ref _data, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
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

    public PanelScheduleViewModel(string projectName, DocsViewModel parent)
    {
        _parent = parent;
        ProjectName = projectName;

        GenerateCommand = new RelayCommand(ExecuteGenerate,
            () => !IsGenerating && _data?.Allocation?.AllPanels.Count > 0);
    }

    public void LoadData(PanelScheduleData data)
    {
        Data = data;

        if (data?.Allocation == null || data.Allocation.AllPanels.Count == 0)
        {
            SummaryText = "No panel data available. Run TurboZones first.";
            return;
        }

        var panels = data.Allocation.AllPanels;
        int moduleCount = panels.Sum(p => p.Modules.Count);
        int loadCount = panels.Sum(p => p.Modules.Sum(m => m.CircuitNumbers.Count));
        SummaryText = $"{panels.Count} panel{(panels.Count == 1 ? "" : "s")}, " +
                      $"{moduleCount} module{(moduleCount == 1 ? "" : "s")}, " +
                      $"{loadCount} load{(loadCount == 1 ? "" : "s")}";
    }

    public void SaveSettings()
    {
        // No panel-schedule-specific settings to persist yet.
    }

    private async void ExecuteGenerate()
    {
        if (_data == null) return;

        var saveDialog = new SaveFileDialog
        {
            Filter = "PDF Files|*.pdf",
            FileName = $"{ProjectName} Panel Schedule.pdf"
        };
        if (saveDialog.ShowDialog() != true) return;

        _parent.SaveSettings();
        IsGenerating = true;
        Progress = 0;

        try
        {
            StatusText = "Generating panel schedule...";
            Progress = 50;

            string outputPath = saveDialog.FileName;
            var settings = new DocsSettings
            {
                LogoFilePath = _parent.LogoFilePath,
                CompanyAddress = _parent.CompanyAddress,
                CompanyPhone = _parent.CompanyPhone,
                CompanyEmail = _parent.CompanyEmail,
                CompanyWebsite = _parent.CompanyWebsite,
                FooterDate = _parent.HeaderDate.ToString("yyyy.MM.dd"),
            };
            await Task.Run(() => PanelSchedulePdfService.Generate(_data, ProjectName, outputPath, settings));

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
