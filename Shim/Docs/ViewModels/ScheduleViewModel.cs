using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Docs.ViewModels;

public class ScheduleViewModel : ViewModelBase
{
    public static readonly string[] DefaultSpecificationNotes =
    [
        "Electrical Contractor to determine fixture housing rating (IC, Non-IC or Remodel) unless otherwise noted.",
        "No substitutions permitted without prior approval from Creative Designs in Lighting.",
        "All recessed trims and/or trim rings shall be painted to match color of ceiling.",
        "Electrical Contractor to field measure all linear lighting prior to order.",
        "All fixture finish colors to be verified with Architect / Interior Designer prior to order.",
        "All Kelvin (color) temperatures to be verified with Architect / Interior Designer prior to order.",
    ];

    private readonly DocsViewModel _parent;
    private double _progress;
    private string _statusText = string.Empty;
    private bool _isGenerating;
    private string _specNote1 = string.Empty;
    private string _specNote2 = string.Empty;
    private string _specNote3 = string.Empty;
    private string _specNote4 = string.Empty;
    private string _specNote5 = string.Empty;
    private string _specNote6 = string.Empty;

    public string ProjectName { get; }
    public ObservableCollection<ScheduleFixtureModel> Fixtures { get; }

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

    public string SpecNote1 { get => _specNote1; set => SetProperty(ref _specNote1, value); }
    public string SpecNote2 { get => _specNote2; set => SetProperty(ref _specNote2, value); }
    public string SpecNote3 { get => _specNote3; set => SetProperty(ref _specNote3, value); }
    public string SpecNote4 { get => _specNote4; set => SetProperty(ref _specNote4, value); }
    public string SpecNote5 { get => _specNote5; set => SetProperty(ref _specNote5, value); }
    public string SpecNote6 { get => _specNote6; set => SetProperty(ref _specNote6, value); }

    public RelayCommand SelectAllCommand { get; }
    public RelayCommand DeselectAllCommand { get; }
    public RelayCommand GenerateCommand { get; }

    public ScheduleViewModel(string projectName, DocsViewModel parent)
    {
        _parent = parent;
        ProjectName = projectName;
        Fixtures = new ObservableCollection<ScheduleFixtureModel>();

        SelectAllCommand = new RelayCommand(() => SetAllSelected(true));
        DeselectAllCommand = new RelayCommand(() => SetAllSelected(false));
        GenerateCommand = new RelayCommand(ExecuteGenerate, () => !IsGenerating && Fixtures.Any(f => f.IsSelected));
    }

    public void LoadFixtures(System.Collections.Generic.List<ScheduleFixtureModel> fixtures)
    {
        Fixtures.Clear();
        foreach (var f in CollapseIdenticalVariants(fixtures))
            Fixtures.Add(f);

        var settings = DocsSettingsService.Load();
        if (settings.ScheduleSelectedTypeMarks.Count > 0)
        {
            foreach (var fixture in Fixtures)
                fixture.IsSelected = settings.ScheduleSelectedTypeMarks.Contains(fixture.TypeMark);
        }

        // Load specification notes (fall back to defaults if none saved)
        var notes = settings.SpecificationNotes;
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
        settings.ScheduleSelectedTypeMarks = Fixtures
            .Where(f => f.IsSelected)
            .Select(f => f.TypeMark)
            .Distinct()
            .ToList();
        settings.SpecificationNotes = [SpecNote1, SpecNote2, SpecNote3, SpecNote4, SpecNote5, SpecNote6];
        DocsSettingsService.Save(settings);
    }

    // Collapse multiple families that share a Type Mark into one row, but only
    // when every spec field matches across the group. Variants that disagree
    // stay as separate rows so the discrepancy is visible to the user.
    private static System.Collections.Generic.IEnumerable<ScheduleFixtureModel> CollapseIdenticalVariants(
        System.Collections.Generic.List<ScheduleFixtureModel> fixtures)
    {
        foreach (var group in fixtures.GroupBy(f => f.TypeMark))
        {
            var rows = group.ToList();
            if (rows.Count == 1 || rows.Skip(1).All(r => SpecFieldsMatch(rows[0], r)))
            {
                yield return rows[0];
            }
            else
            {
                foreach (var r in rows)
                {
                    r.IsDuplicateTypeMark = true;
                    yield return r;
                }
            }
        }
    }

    private static bool SpecFieldsMatch(ScheduleFixtureModel a, ScheduleFixtureModel b) =>
        a.Classification == b.Classification &&
        a.CatalogNumber == b.CatalogNumber &&
        a.Manufacturer == b.Manufacturer &&
        a.Description1 == b.Description1 &&
        a.Description2 == b.Description2 &&
        a.Finish == b.Finish &&
        a.Listings == b.Listings &&
        a.Mounting == b.Mounting &&
        a.Dimming == b.Dimming &&
        a.Watts == b.Watts &&
        a.Volts == b.Volts &&
        a.Lumens == b.Lumens &&
        a.CCT == b.CCT &&
        a.CRI == b.CRI &&
        a.ScheduleNotes.SequenceEqual(b.ScheduleNotes);

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
            FileName = $"{ProjectName} Fixture Schedule.pdf"
        };
        if (saveDialog.ShowDialog() != true) return;

        _parent.SaveSettings();
        IsGenerating = true;
        Progress = 0;

        try
        {
            StatusText = "Generating schedule...";
            Progress = 50;

            string outputPath = saveDialog.FileName;
            bool largeFormat = _parent.UseLargeFormat;
            var settings = new DocsSettings
            {
                LogoFilePath = _parent.LogoFilePath,
                CompanyAddress = _parent.CompanyAddress,
                CompanyPhone = _parent.CompanyPhone,
                CompanyEmail = _parent.CompanyEmail,
                CompanyWebsite = _parent.CompanyWebsite,
                FooterDate = _parent.HeaderDate.ToString("yyyy.MM.dd"),
                SpecificationNotes = [SpecNote1, SpecNote2, SpecNote3, SpecNote4, SpecNote5, SpecNote6],
            };
            await Task.Run(() => SchedulePdfService.Generate(selected, ProjectName, outputPath, largeFormat, settings));

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
