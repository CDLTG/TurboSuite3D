#nullable disable
using System.Windows.Input;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Name.ViewModels;

public class TurboNameViewModel : ViewModelBase
{
    private int _regionCount;
    private int _cadEntryCount;
    private bool _shouldRun;

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

    /// <summary>
    /// Set to true when the user clicks Run; the command reads this after ShowDialog returns.
    /// </summary>
    public bool ShouldRun
    {
        get => _shouldRun;
        set => SetProperty(ref _shouldRun, value);
    }

    public ICommand RunAssignCommand { get; }

    public TurboNameViewModel()
    {
        RunAssignCommand = new RelayCommand(ExecuteRun, () => RegionCount > 0 && CadEntryCount > 0);
    }

    private void ExecuteRun()
    {
        ShouldRun = true;
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Raised when the ViewModel wants to close the window (after Run is clicked).
    /// </summary>
    public event System.Action CloseRequested;
}
