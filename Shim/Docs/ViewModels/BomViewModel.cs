using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Docs.ViewModels;

public class BomViewModel : ViewModelBase
{
    private readonly DocsViewModel _parent;
    private BomData? _data;
    private string _summaryText = "No BOM data available. Run TurboZones first.";
    private double _progress;
    private string _statusText = string.Empty;
    private bool _isGenerating;

    public string ProjectName { get; }

    public BomData? Data
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

    public BomViewModel(string projectName, DocsViewModel parent)
    {
        _parent = parent;
        ProjectName = projectName;

        GenerateCommand = new RelayCommand(ExecuteGenerate,
            () => !IsGenerating && _data?.Items.Count > 0);
    }

    public void LoadData(BomData data)
    {
        Data = data;

        if (data?.Items == null || data.Items.Count == 0)
        {
            SummaryText = "No BOM data available. Run TurboZones first.";
            return;
        }

        int lineItems = data.Items.Count(i => !i.IsHeader);
        int categories = data.Items.Count(i => i.IsHeader);
        SummaryText = $"{lineItems} item{(lineItems == 1 ? "" : "s")} across " +
                      $"{categories} categor{(categories == 1 ? "y" : "ies")} ({data.BrandName})";
    }

    public void SaveSettings()
    {
        // No BOM-specific settings to persist yet.
    }

    private async void ExecuteGenerate()
    {
        if (_data == null) return;

        var saveDialog = new SaveFileDialog
        {
            Filter = "PDF Files|*.pdf",
            FileName = $"{ProjectName} Bill of Materials.pdf"
        };
        if (saveDialog.ShowDialog() != true) return;

        _parent.SaveSettings();
        IsGenerating = true;
        Progress = 0;

        try
        {
            StatusText = "Generating bill of materials...";
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
            await Task.Run(() => BomPdfService.Generate(_data.Items, ProjectName, _data.BrandName, outputPath, settings));

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
