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
    private bool _useLargeFormat = true;
    private double _progress;
    private string _statusText = string.Empty;
    private bool _isGenerating;

    public string ProjectName { get; }
    public ObservableCollection<ScheduleFixtureModel> Fixtures { get; }

    public bool UseLargeFormat
    {
        get => _useLargeFormat;
        set => SetProperty(ref _useLargeFormat, value);
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

    public RelayCommand SelectAllCommand { get; }
    public RelayCommand DeselectAllCommand { get; }
    public RelayCommand GenerateCommand { get; }

    public ScheduleViewModel(string projectName)
    {
        ProjectName = projectName;
        Fixtures = new ObservableCollection<ScheduleFixtureModel>();

        var settings = DocsSettingsService.Load();
        _useLargeFormat = settings.ScheduleUseLargeFormat;

        SelectAllCommand = new RelayCommand(() => SetAllSelected(true));
        DeselectAllCommand = new RelayCommand(() => SetAllSelected(false));
        GenerateCommand = new RelayCommand(ExecuteGenerate, () => !IsGenerating && Fixtures.Any(f => f.IsSelected));
    }

    public void LoadFixtures(System.Collections.Generic.List<ScheduleFixtureModel> fixtures)
    {
        Fixtures.Clear();
        foreach (var f in fixtures)
            Fixtures.Add(f);

        var settings = DocsSettingsService.Load();
        if (settings.ScheduleSelectedTypeMarks.Count > 0)
        {
            foreach (var fixture in Fixtures)
                fixture.IsSelected = settings.ScheduleSelectedTypeMarks.Contains(fixture.TypeMark);
        }
    }

    public void SaveSettings()
    {
        var settings = DocsSettingsService.Load();
        settings.ScheduleUseLargeFormat = UseLargeFormat;
        settings.ScheduleSelectedTypeMarks = Fixtures
            .Where(f => f.IsSelected)
            .Select(f => f.TypeMark)
            .ToList();
        DocsSettingsService.Save(settings);
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
            FileName = $"{ProjectName} Fixture Schedule.pdf"
        };
        if (saveDialog.ShowDialog() != true) return;

        SaveSettings();
        IsGenerating = true;
        Progress = 0;

        try
        {
            StatusText = "Generating schedule...";
            Progress = 50;

            string outputPath = saveDialog.FileName;
            bool largeFormat = UseLargeFormat;
            await Task.Run(() => SchedulePdfService.Generate(selected, ProjectName, outputPath, largeFormat));

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
