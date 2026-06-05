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
    private readonly RegionPickHandler _handler;

    private int _createdCount;
    private int _failedCount;
    private bool _isPicking;
    private string _statusText = "";
    private string _pickingHint = "";

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

    public string PickingHint
    {
        get => _pickingHint;
        set => SetProperty(ref _pickingHint, value);
    }

    public ICommand RectangleCommand { get; }
    public ICommand PolygonCommand { get; }
    public ICommand FinishCommand { get; }

    public event Action CloseRequested;

    public GenerateRegionsViewModel(ExternalEvent externalEvent, RegionPickHandler handler)
    {
        _externalEvent = externalEvent;
        _handler = handler;

        RectangleCommand = new RelayCommand(OnRectangle, () => !IsPicking);
        PolygonCommand = new RelayCommand(OnPolygon, () => !IsPicking);
        FinishCommand = new RelayCommand(OnFinish, () => !IsPicking);
    }

    private void OnRectangle()
    {
        IsPicking = true;
        PickingHint = "Click two corners to draw a rectangle. Escape to pause.";
        RaisePick(new RectanglePickRequest());
    }

    private void OnPolygon()
    {
        IsPicking = true;
        PickingHint = "Click corners to trace a room. Escape to close shape. Escape again to pause.";
        RaisePick(new PolygonPickRequest());
    }

    private void OnFinish()
    {
        CloseRequested?.Invoke();
    }

    private void RaisePick(RegionGenerationRequest request)
    {
        request.OnComplete = result =>
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
        };
        _handler.CurrentRequest = request;
        _externalEvent.Raise();
    }
}
