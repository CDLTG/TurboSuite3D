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

public class NotesViewModel : ViewModelBase
{
    public static readonly string[] DefaultGeneralNotes =
    [
        "THESE DRAWINGS AND SPECIFICATIONS ARE INTENDED TO PROVIDE A COMPLETE AND OPERATIONAL LIGHTING SYSTEM. THE ELECTRICAL CONTRACTOR SHALL PROVIDE AND INSTALL ALL NECESSARY MATERIALS AND EQUIPMENT. NO SUBSTITUTIONS SHALL BE MADE WITHOUT PRIOR WRITTEN APPROVAL FROM CREATIVE DESIGNS IN LIGHTING.",
        "THE GENERAL CONTRACTOR AND ELECTRICAL CONTRACTOR SHALL REVIEW ALL INFORMATION ON THESE PLANS. ERRORS, OMISSIONS, OR QUESTIONS SHALL BE REPORTED TO CREATIVE DESIGNS IN LIGHTING BEFORE PROCEEDING.",
        "THE NATIONAL ELECTRICAL CODE (NEC), ALL APPLICABLE STATE AND LOCAL CODES, AND THE ELECTRICAL SPECIFICATIONS PREPARED BY THE PROJECT ELECTRICAL ENGINEER SHALL GOVERN THE MINIMUM STANDARD OF WORK. WHERE CONFLICT EXISTS BETWEEN THESE PLANS AND APPLICABLE CODE, THE CODE SHALL PREVAIL.",
        "ALL EQUIPMENT SHALL BE INSTALLED IN ACCORDANCE WITH MANUFACTURER REQUIREMENTS UNLESS OTHERWISE INDICATED.",
        "THE ELECTRICAL CONTRACTOR SHALL FIELD-VERIFY ALL EXISTING CONDITIONS, DIMENSIONS, AND EQUIPMENT BEFORE ORDERING MATERIALS. DISCREPANCIES SHALL BE REPORTED TO CREATIVE DESIGNS IN LIGHTING BEFORE PROCEEDING.",
        "THE ELECTRICAL CONTRACTOR SHALL COORDINATE INSTALLATION OF ALL LIGHTING EQUIPMENT WITH THE GENERAL CONTRACTOR AND ALL APPLICABLE SUBCONTRACTORS PRIOR TO ROUGH-IN.",
        "RECEPTACLES AND FLOOR OUTLETS SHOWN ON THE LIGHTING PLANS ARE FOR LIGHTING CONTROL ONLY. REFER TO THE ELECTRICAL PLANS FOR ALL OTHER DEVICE LOCATIONS AND WIRING REQUIREMENTS.",
        "RECESSED FLOOR OUTLETS SHALL BE SPECIFIED AND LOCATED BY THE OWNER AND INTERIOR DESIGNER AND INSTALLED BY THE ELECTRICAL CONTRACTOR.",
        "ALL LIGHTING FIXTURES AND EQUIPMENT SHALL BE INSTALLED TO PROVIDE ADEQUATE ACCESS FOR MAINTENANCE WHILE MAINTAINING CLEARANCES PER NEC AND MANUFACTURER REQUIREMENTS.",
        "ALL WIRE SHALL BE COPPER, SIZED BY THE ELECTRICAL CONTRACTOR PER NEC AND MANUFACTURER REQUIREMENTS.",
        "THE ELECTRICAL CONTRACTOR SHALL COORDINATE ALL SWITCH AND DIMMER LOCATIONS WITH THE OWNER AND ARCHITECT. ALL DEVICES SHALL MATCH THE FINISH SPECIFIED BY THE ARCHITECT. ALL DIMMERS SHALL BE SIZED AND COMPATIBLE WITH THE DIMMING SYSTEM AS SPECIFIED.",
        "THE ELECTRICAL CONTRACTOR SHALL PROVIDE OVERCURRENT PROTECTION FOR ALL REMOTE POWER SUPPLIES. SECONDARY WIRING SHALL BE SIZED TO MINIMIZE VOLTAGE DROP. FINAL LOCATIONS SHALL BE VERIFIED IN THE FIELD WITH THE GENERAL CONTRACTOR AND OWNER'S REPRESENTATIVE.",
        "DECORATIVE FIXTURES AND CEILING FANS SHALL BE SPECIFIED AND PROCURED BY THE OWNER AND INTERIOR DESIGNER AND INSTALLED BY THE ELECTRICAL CONTRACTOR. FINAL LOCATIONS SHALL BE VERIFIED WITH THE OWNER AND INTERIOR DESIGNER.",
        "ALL RECESSED TRIMS, TRIM RINGS, AND EXPOSED EQUIPMENT SHALL BE PAINTED TO MATCH ADJACENT SURFACES, INTERIOR AND EXTERIOR.",
        "THE ELECTRICAL CONTRACTOR'S BID SHALL INCLUDE TWO ELECTRICIANS FOR TWO EVENINGS, FOUR-HOUR MINIMUM EACH, FOR FINAL AIM AND FOCUS OF ALL ADJUSTABLE LIGHTING FIXTURES AND SCENE SETTING. THE CONTRACTOR SHALL PROVIDE ALL NECESSARY EQUIPMENT AND ACCESS.",
    ];

    public static readonly string[] DefaultControlNotes =
    [
        "THE ELECTRICAL CONTRACTOR SHALL BE CERTIFIED BY THE CONTROL SYSTEM MANUFACTURER PRIOR TO THE INSTALLATION OF ANY ROUGH-IN COMPONENTS.",
        "THE MAXIMUM NUMBER OF DEVICES PER HOMERUN SHALL BE TEN.",
        "THE ELECTRICAL CONTRACTOR SHALL PROVIDE AND INSTALL SURGE SUPPRESSION FOR ALL CONTROL EQUIPMENT PER MANUFACTURER REQUIREMENTS.",
        "THE ELECTRICAL CONTRACTOR SHALL INSTALL A PROPERLY SIZED WIREWAY AT EACH CONTROL PANEL LOCATION.",
        "THE ELECTRICAL CONTRACTOR SHALL PROVIDE AND INSTALL TWO ETHERNET DROPS AT EACH PROCESSOR LOCATION FROM THE HOME NETWORK SWITCH.",
        "THE ELECTRICAL CONTRACTOR SHALL DAISY-CHAIN TWO CONTROL SYSTEM WIRES AND TWO ETHERNET WIRES BETWEEN ALL CONTROL PANELS PER MANUFACTURER REQUIREMENTS.",
        "ALL KEYPADS SHALL BE INSTALLED IN PLASTIC WALL BOXES. MOUNTING HEIGHT SHALL BE VERIFIED WITH THE OWNER AND ARCHITECT DURING ROUGH-IN. WHERE CONFIRMATION IS NOT POSSIBLE, CENTER AT 50 INCHES A.F.F.",
        "THE ELECTRICAL CONTRACTOR SHALL VERIFY A FLAT MOUNTING SURFACE AT ALL KEYPAD LOCATIONS PRIOR TO INSTALLATION. SURFACE ISSUES SHALL BE REPORTED TO THE GENERAL CONTRACTOR BEFORE PROCEEDING.",
        "THE ELECTRICAL CONTRACTOR SHALL RECORD THE STATION NUMBER INSIDE EACH KEYPAD ROUGH-IN BOX AND THE SWITCH LEG NUMBER INSIDE EACH ROUGH-IN FIXTURE CAN WITH PERMANENT MARKER.",
        "STYLE, COLOR, AND ENGRAVING OF ALL KEYPADS SHALL BE DETERMINED WITH THE OWNER AND INTERIOR DESIGNER PRIOR TO ORDERING.",
        "THE ELECTRICAL CONTRACTOR SHALL PROVIDE SCREWLESS COVERS FOR ALL INTERIOR KEYPADS WHERE APPLICABLE AND WATERPROOF COVERS FOR ALL EXTERIOR KEYPADS.",
        "THE ELECTRICAL CONTRACTOR SHALL PROVIDE AND INSTALL ALL WIRING AND EQUIPMENT REQUIRED FOR SHADE AND DRAPE MOTORS. ALL REQUIREMENTS SHALL BE COORDINATED WITH THE GENERAL CONTRACTOR AND SHADE OR DRAPE CONTRACTOR.",
        "THE ELECTRICAL CONTRACTOR'S BID SHALL INCLUDE ONE ELECTRICIAN FOR TWO EVENINGS, FOUR-HOUR MINIMUM EACH, TO ASSIST DURING FINAL PROGRAMMING. THE CONTRACTOR SHALL PROVIDE ALL NECESSARY EQUIPMENT AND ACCESS.",
    ];

    private readonly DocsViewModel _parent;
    private bool _isFixturePackage = true;
    private double _progress;
    private string _statusText = string.Empty;
    private bool _isGenerating;
    private string _generalSource = string.Empty;
    private string _controlSource = string.Empty;

    public string ProjectName { get; }

    public ObservableCollection<NoteItem> GeneralNoteItems { get; } = new();
    public ObservableCollection<NoteItem> ControlNoteItems { get; } = new();

    public string GeneralSource
    {
        get => _generalSource;
        private set => SetProperty(ref _generalSource, value);
    }

    public string ControlSource
    {
        get => _controlSource;
        private set => SetProperty(ref _controlSource, value);
    }

    public bool IsFixturePackage
    {
        get => _isFixturePackage;
        set
        {
            if (SetProperty(ref _isFixturePackage, value))
            {
                OnPropertyChanged(nameof(IsControlPackage));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsControlPackage
    {
        get => !_isFixturePackage;
        set => IsFixturePackage = !value;
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

    public NotesViewModel(string projectName, DocsViewModel parent)
    {
        _parent = parent;
        ProjectName = projectName;

        GenerateCommand = new RelayCommand(ExecuteGenerate, () => !IsGenerating);
    }

    public void LoadNotes(List<string> generalNotes, List<string> controlNotes)
    {
        GeneralNoteItems.Clear();
        var general = generalNotes.Count > 0 ? generalNotes : DefaultGeneralNotes.ToList();
        GeneralSource = generalNotes.Count > 0
            ? $"Reading from Notes_General schedule ({general.Count} notes)"
            : "Schedule \"Notes_General\" not found — using defaults";
        for (int i = 0; i < general.Count; i++)
            GeneralNoteItems.Add(new NoteItem(i + 1) { Text = general[i] });

        ControlNoteItems.Clear();
        var control = controlNotes.Count > 0 ? controlNotes : DefaultControlNotes.ToList();
        ControlSource = controlNotes.Count > 0
            ? $"Reading from Notes_Controls schedule ({control.Count} notes)"
            : "Schedule \"Notes_Controls\" not found — using defaults";
        for (int i = 0; i < control.Count; i++)
            ControlNoteItems.Add(new NoteItem(i + 1) { Text = control[i] });
    }

    public void SaveSettings()
    {
        // Notes are read from Revit schedules — nothing to persist.
    }

    private async void ExecuteGenerate()
    {
        bool isFixture = IsFixturePackage;
        string label = isFixture ? "General Notes" : "Control Notes";
        var notes = isFixture
            ? GeneralNoteItems.Select(n => n.Text).ToList()
            : ControlNoteItems.Select(n => n.Text).ToList();

        var saveDialog = new SaveFileDialog
        {
            Filter = "PDF Files|*.pdf",
            FileName = $"{ProjectName} {label}.pdf"
        };
        if (saveDialog.ShowDialog() != true) return;

        _parent.SaveSettings();
        IsGenerating = true;
        Progress = 0;

        try
        {
            StatusText = $"Generating {label.ToLower()}...";
            Progress = 50;

            string outputPath = saveDialog.FileName;
            var settings = new DocsSettings
            {
                LogoFilePath = _parent.LogoFilePath,
                CompanyAddress = _parent.CompanyAddress,
                CompanyPhone = _parent.CompanyPhone,
                CompanyEmail = _parent.CompanyEmail,
                CompanyWebsite = _parent.CompanyWebsite,
                CoverBrandingVerticalPath = _parent.CoverBrandingVerticalPath,
                CoverBrandingHorizontalPath = _parent.CoverBrandingHorizontalPath,
                ProjectLocation = _parent.ProjectLocation,
                HeaderDate = _parent.HeaderDate.ToString("MMMM d, yyyy"),
            };
            string projectNumber = _parent.ProjectNumber;
            await Task.Run(() => NotesPdfService.Generate(notes, ProjectName, label, outputPath, settings, projectNumber, isFixture));

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

public class NoteItem : ViewModelBase
{
    private string _text = string.Empty;

    public int Number { get; }
    public string Label => $"{Number})";

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public NoteItem(int number)
    {
        Number = number;
    }
}
