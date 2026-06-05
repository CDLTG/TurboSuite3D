using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using PdfSharpCore.Pdf;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Docs.ViewModels;

public class PowerSuppliesViewModel : ViewModelBase
{
    public static readonly string[] DefaultSpecificationNotes =
    [
        "All remote power supplies shall be installed per manufacturer's requirements.",
        "Maximum wire run lengths to be verified with manufacturer prior to installation.",
        "Electrical Contractor to verify circuit capacity before connecting power supplies.",
        "All power supply locations to be coordinated with General Contractor prior to rough-in.",
        "Power supply mounting heights and locations per lighting plan unless otherwise noted.",
        "No substitutions permitted without prior approval from the Lighting Designer.",
    ];

    private readonly DocsViewModel _parent;
    private double _progress;
    private string _statusText = string.Empty;
    private bool _isGenerating;
    private int _outputMode;
    private bool _useLargeFormat;
    private string _specNote1 = string.Empty;
    private string _specNote2 = string.Empty;
    private string _specNote3 = string.Empty;
    private string _specNote4 = string.Empty;
    private string _specNote5 = string.Empty;
    private string _specNote6 = string.Empty;

    public string ProjectName { get; }
    public ObservableCollection<RPSScheduleModel> Items { get; }
    public List<RPSInstanceModel> Instances { get; private set; } = [];

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

    /// <summary>
    /// 0 = RPS Schedule only, 1 = Lookup Table only, 2 = Both
    /// </summary>
    public int OutputMode
    {
        get => _outputMode;
        set => SetProperty(ref _outputMode, value);
    }

    public bool UseLargeFormat
    {
        get => _useLargeFormat;
        set => SetProperty(ref _useLargeFormat, value);
    }

    public string SpecNote1 { get => _specNote1; set => SetProperty(ref _specNote1, value); }
    public string SpecNote2 { get => _specNote2; set => SetProperty(ref _specNote2, value); }
    public string SpecNote3 { get => _specNote3; set => SetProperty(ref _specNote3, value); }
    public string SpecNote4 { get => _specNote4; set => SetProperty(ref _specNote4, value); }
    public string SpecNote5 { get => _specNote5; set => SetProperty(ref _specNote5, value); }
    public string SpecNote6 { get => _specNote6; set => SetProperty(ref _specNote6, value); }

    public RelayCommand SelectAllCommand { get; }
    public RelayCommand DeselectAllCommand { get; }
    public RelayCommand GenerateCommand { get; }

    public PowerSuppliesViewModel(string projectName, DocsViewModel parent)
    {
        _parent = parent;
        ProjectName = projectName;
        Items = new ObservableCollection<RPSScheduleModel>();

        SelectAllCommand = new RelayCommand(() => SetAllSelected(true));
        DeselectAllCommand = new RelayCommand(() => SetAllSelected(false));
        GenerateCommand = new RelayCommand(ExecuteGenerate, CanGenerate);
    }

    public void LoadData(List<RPSScheduleModel> scheduleItems, List<RPSInstanceModel> instances)
    {
        Items.Clear();
        foreach (var item in scheduleItems)
            Items.Add(item);
        Instances = instances;

        var settings = DocsSettingsService.Load();
        OutputMode = settings.RPSOutputMode;
        UseLargeFormat = settings.RPSUseLargeFormat;

        if (settings.RPSSelectedTypeMarks.Count > 0)
        {
            foreach (var item in Items)
                item.IsSelected = settings.RPSSelectedTypeMarks.Contains(item.TypeMark);
        }

        var notes = settings.RPSSpecificationNotes;
        SpecNote1 = notes.Count > 0 ? notes[0] : DefaultSpecificationNotes[0];
        SpecNote2 = notes.Count > 1 ? notes[1] : DefaultSpecificationNotes[1];
        SpecNote3 = notes.Count > 2 ? notes[2] : DefaultSpecificationNotes[2];
        SpecNote4 = notes.Count > 3 ? notes[3] : DefaultSpecificationNotes[3];
        SpecNote5 = notes.Count > 4 ? notes[4] : DefaultSpecificationNotes[4];
        SpecNote6 = notes.Count > 5 ? notes[5] : DefaultSpecificationNotes[5];
    }

    public void SaveSettings()
    {
        var settings = DocsSettingsService.Load();
        settings.RPSSelectedTypeMarks = Items
            .Where(i => i.IsSelected)
            .Select(i => i.TypeMark)
            .ToList();
        settings.RPSOutputMode = OutputMode;
        settings.RPSUseLargeFormat = UseLargeFormat;
        settings.RPSSpecificationNotes = [SpecNote1, SpecNote2, SpecNote3, SpecNote4, SpecNote5, SpecNote6];
        DocsSettingsService.Save(settings);
    }

    private bool CanGenerate()
    {
        if (IsGenerating) return false;
        // Lookup-only mode just needs instances
        if (OutputMode == 1) return Instances.Count > 0;
        // Schedule or combined mode needs selected items
        return Items.Any(i => i.IsSelected);
    }

    private void SetAllSelected(bool selected)
    {
        foreach (var item in Items)
            item.IsSelected = selected;
    }

    private async void ExecuteGenerate()
    {
        var saveDialog = new SaveFileDialog
        {
            Filter = "PDF Files|*.pdf",
            FileName = $"{ProjectName} Power Supplies.pdf"
        };
        if (saveDialog.ShowDialog() != true) return;

        _parent.SaveSettings();
        IsGenerating = true;
        Progress = 0;

        try
        {
            string outputPath = saveDialog.FileName;
            var docsSettings = new DocsSettings
            {
                LogoFilePath = _parent.LogoFilePath,
                CompanyAddress = _parent.CompanyAddress,
                CompanyPhone = _parent.CompanyPhone,
                CompanyEmail = _parent.CompanyEmail,
                CompanyWebsite = _parent.CompanyWebsite,
                RPSSpecificationNotes = [SpecNote1, SpecNote2, SpecNote3, SpecNote4, SpecNote5, SpecNote6],
            };

            var selected = Items.Where(i => i.IsSelected).ToList();

            await Task.Run(() =>
            {
                switch (OutputMode)
                {
                    case 0: // Schedule only
                        StatusText = "Generating RPS schedule...";
                        Progress = 50;
                        RPSSchedulePdfService.Generate(selected, ProjectName, outputPath, UseLargeFormat, docsSettings);
                        break;

                    case 1: // Lookup table only
                        StatusText = "Generating lookup table...";
                        Progress = 50;
                        RPSLookupPdfService.Generate(Instances, ProjectName, outputPath, docsSettings);
                        break;

                    case 2: // Combined
                        StatusText = "Generating RPS schedule...";
                        Progress = 30;
                        using (var pdf = new PdfDocument())
                        {
                            pdf.Info.Title = $"{ProjectName} Power Supplies";
                            RPSSchedulePdfService.GeneratePages(pdf, selected, ProjectName, UseLargeFormat, docsSettings);
                            StatusText = "Generating lookup table...";
                            Progress = 70;
                            RPSLookupPdfService.GeneratePages(pdf, Instances, ProjectName, docsSettings);
                            pdf.Save(outputPath);
                        }
                        break;
                }
            });

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
