using System.Collections.Generic;
using TurboSuite.Docs.Models;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Docs.ViewModels;

public class DocsViewModel : ViewModelBase
{
    private int _selectedTabIndex;
    private string _statusText = string.Empty;

    public string ProjectName { get; }
    public CutSheetsViewModel CutSheetsVM { get; }
    public ScheduleViewModel ScheduleVM { get; }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public RelayCommand GenerateCommand { get; }

    public DocsViewModel(List<FixtureSpecModel> cutSheetFixtures, string projectName)
    {
        ProjectName = projectName;

        CutSheetsVM = new CutSheetsViewModel(cutSheetFixtures, projectName);
        ScheduleVM = new ScheduleViewModel(projectName);

        // Forward status text from active tab VM
        CutSheetsVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CutSheetsViewModel.StatusText))
                StatusText = CutSheetsVM.StatusText;
        };
        ScheduleVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ScheduleViewModel.StatusText))
                StatusText = ScheduleVM.StatusText;
        };

        GenerateCommand = new RelayCommand(ExecuteGenerate, CanGenerate);

        // Re-evaluate CanGenerate when tab changes or generating state changes
        CutSheetsVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CutSheetsViewModel.IsGenerating))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        };
        ScheduleVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ScheduleViewModel.IsGenerating))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        };
    }

    public void SaveSettings()
    {
        CutSheetsVM.SaveSettings();
        ScheduleVM.SaveSettings();
    }

    private bool CanGenerate()
    {
        return SelectedTabIndex switch
        {
            0 => ScheduleVM.GenerateCommand.CanExecute(null),
            1 => CutSheetsVM.GenerateCommand.CanExecute(null),
            _ => false,
        };
    }

    private void ExecuteGenerate()
    {
        switch (SelectedTabIndex)
        {
            case 0:
                ScheduleVM.GenerateCommand.Execute(null);
                break;
            case 1:
                CutSheetsVM.GenerateCommand.Execute(null);
                break;
        }
    }
}
