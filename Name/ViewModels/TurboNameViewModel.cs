#nullable disable
using System.Windows.Input;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Name.ViewModels;

public class TurboNameViewModel : ViewModelBase
{
    private int _regionCount;
    private int _cadEntryCount;
    private int _wallSegmentCount;
    private bool _shouldRun;
    private bool _shouldGenerate;

    public int RegionCount
    {
        get => _regionCount;
        set => SetProperty(ref _regionCount, value);
    }

    public int CadEntryCount
    {
        get => _cadEntryCount;
        set => SetProperty(ref _cadEntryCount, value);
    }

    public int WallSegmentCount
    {
        get => _wallSegmentCount;
        set => SetProperty(ref _wallSegmentCount, value);
    }

    /// <summary>
    /// Set to true when the user clicks Run; the command reads this after ShowDialog returns.
    /// </summary>
    public bool ShouldRun
    {
        get => _shouldRun;
        set => SetProperty(ref _shouldRun, value);
    }

    /// <summary>
    /// Set to true when the user clicks Generate; the command reads this after ShowDialog returns.
    /// </summary>
    public bool ShouldGenerate
    {
        get => _shouldGenerate;
        set => SetProperty(ref _shouldGenerate, value);
    }

    public ICommand RunAssignCommand { get; }
    public ICommand RunGenerateCommand { get; }

    public TurboNameViewModel()
    {
        RunAssignCommand = new RelayCommand(ExecuteRun, () => RegionCount > 0 && CadEntryCount > 0);
        RunGenerateCommand = new RelayCommand(ExecuteGenerate, () => WallSegmentCount > 0);
    }

    private void ExecuteRun()
    {
        ShouldRun = true;
        CloseRequested?.Invoke();
    }

    private void ExecuteGenerate()
    {
        ShouldGenerate = true;
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Raised when the ViewModel wants to close the window (after Run or Generate is clicked).
    /// </summary>
    public event System.Action CloseRequested;
}
