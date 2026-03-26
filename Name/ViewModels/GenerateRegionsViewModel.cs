#nullable disable
using System;
using System.Windows.Input;
using Autodesk.Revit.UI;
using TurboSuite.Name.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Name.ViewModels;

public class GenerateRegionsViewModel : ViewModelBase
{
    private readonly ExternalEvent _externalEvent;
    private readonly RegionGenerationHandler _handler;

    private int _createdCount;
    private int _failedCount;
    private bool _isPicking;
    private string _statusText = "";

    public int CreatedCount
    {
        get => _createdCount;
        set => SetProperty(ref _createdCount, value);
    }

    public int FailedCount
    {
        get => _failedCount;
        set => SetProperty(ref _failedCount, value);
    }

    public bool IsPicking
    {
        get => _isPicking;
        set => SetProperty(ref _isPicking, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public ICommand ResumeCommand { get; }
    public ICommand FinishCommand { get; }

    public event Action CloseRequested;

    public GenerateRegionsViewModel(ExternalEvent externalEvent, RegionGenerationHandler handler)
    {
        _externalEvent = externalEvent;
        _handler = handler;

        ResumeCommand = new RelayCommand(OnResume, () => !IsPicking);
        FinishCommand = new RelayCommand(OnFinish, () => !IsPicking);
    }

    public void StartPicking()
    {
        IsPicking = true;
        RaisePick();
    }

    private void OnResume()
    {
        IsPicking = true;
        RaisePick();
    }

    private void OnFinish()
    {
        CloseRequested?.Invoke();
    }

    private void RaisePick()
    {
        _handler.CurrentRequest = new StartPickingRequest
        {
            OnComplete = result =>
            {
                if (result is PickLoopUpdate update)
                {
                    CreatedCount = update.TotalCreated;
                    FailedCount = update.TotalFailed;
                    if (update.LastStatus != null)
                        StatusText = update.LastStatus;
                    if (update.LoopEnded)
                        IsPicking = false;
                }
            }
        };
        _externalEvent.Raise();
    }
}
