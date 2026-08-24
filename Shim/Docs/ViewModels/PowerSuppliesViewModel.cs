using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using PdfSharp.Pdf;
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
        "Power supplies shall be field-located unless otherwise indicated.",
        "No substitutions permitted without prior approval from the Lighting Designer.",
        "",
        "",
    ];

    private readonly DocsViewModel _parent;
    private double _progress;
    private string _statusText = string.Empty;
    private bool _isGenerating;
    private bool _includeSchedule;
    private bool _includeLookup;
    private bool _includeBreakdown;
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
    public List<RPSBreakdownModel> Breakdown { get; private set; } = [];

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

    /// <summary>Include the per-type RPS schedule pages.</summary>
    public bool IncludeSchedule
    {
        get => _includeSchedule;
        set
        {
            if (SetProperty(ref _includeSchedule, value))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Include the per-instance lookup-table pages.</summary>
    public bool IncludeLookup
    {
        get => _includeLookup;
        set
        {
            if (SetProperty(ref _includeLookup, value))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Include the driver/sub-driver breakdown pages.</summary>
    public bool IncludeBreakdown
    {
        get => _includeBreakdown;
        set
        {
            if (SetProperty(ref _includeBreakdown, value))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
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

    public void LoadData(
        List<RPSScheduleModel> scheduleItems,
        List<RPSInstanceModel> instances,
        List<RPSBreakdownModel> breakdown)
    {
        Items.Clear();
        foreach (var item in scheduleItems)
            Items.Add(item);
        Instances = instances;
        Breakdown = breakdown;

        var settings = DocsSettingsService.Load();

        // One-time migration: seed the Include* checkboxes from the legacy single-select
        // OutputMode (0=Schedule, 1=Lookup, 2=Both) so existing users keep their selection.
        if (!settings.RPSOutputMigrated)
        {
            settings.RPSIncludeSchedule = settings.RPSOutputMode == 0 || settings.RPSOutputMode == 2;
            settings.RPSIncludeLookup = settings.RPSOutputMode == 1 || settings.RPSOutputMode == 2;
            settings.RPSIncludeBreakdown = false;
            settings.RPSOutputMigrated = true;
            DocsSettingsService.Save(settings);
        }

        IncludeSchedule = settings.RPSIncludeSchedule;
        IncludeLookup = settings.RPSIncludeLookup;
        IncludeBreakdown = settings.RPSIncludeBreakdown;
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
        settings.RPSIncludeSchedule = IncludeSchedule;
        settings.RPSIncludeLookup = IncludeLookup;
        settings.RPSIncludeBreakdown = IncludeBreakdown;
        settings.RPSOutputMigrated = true;
        settings.RPSUseLargeFormat = UseLargeFormat;
        settings.RPSSpecificationNotes = [SpecNote1, SpecNote2, SpecNote3, SpecNote4, SpecNote5, SpecNote6];
        DocsSettingsService.Save(settings);
    }

    private bool CanGenerate()
    {
        if (IsGenerating) return false;
        // At least one checked output that actually has data to render.
        return ScheduleReady || LookupReady || BreakdownReady;
    }

    private bool ScheduleReady => IncludeSchedule && Items.Any(i => i.IsSelected);
    private bool LookupReady => IncludeLookup && Instances.Count > 0;
    private bool BreakdownReady => IncludeBreakdown && Breakdown.Count > 0;

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
                FooterDate = _parent.HeaderDate.ToString("yyyy.MM.dd"),
                RPSSpecificationNotes = [SpecNote1, SpecNote2, SpecNote3, SpecNote4, SpecNote5, SpecNote6],
            };

            var selected = Items.Where(i => i.IsSelected).ToList();
            bool doSchedule = ScheduleReady;
            bool doLookup = LookupReady;
            bool doBreakdown = BreakdownReady;

            await Task.Run(() =>
            {
                // Merge each checked-and-ready output into one PDF, in reading order:
                // schedule → lookup table → breakdown.
                using var pdf = new PdfDocument();
                pdf.Info.Title = $"{ProjectName} Power Supplies";

                if (doSchedule)
                {
                    StatusText = "Generating RPS schedule...";
                    Progress = 25;
                    RPSSchedulePdfService.GeneratePages(pdf, selected, ProjectName, UseLargeFormat, docsSettings);
                }

                if (doLookup)
                {
                    StatusText = "Generating lookup table...";
                    Progress = 55;
                    RPSLookupPdfService.GeneratePages(pdf, Instances, ProjectName, UseLargeFormat, docsSettings);
                }

                if (doBreakdown)
                {
                    StatusText = "Generating driver breakdown...";
                    Progress = 80;
                    RPSBreakdownPdfService.GeneratePages(pdf, Breakdown, ProjectName, docsSettings);
                }

                pdf.Save(outputPath);
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
