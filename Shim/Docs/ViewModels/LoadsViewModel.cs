using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Docs.ViewModels;

public class LoadsViewModel : ViewModelBase
{
    private readonly DocsViewModel _parent;
    private bool _isByRoom;
    private double _progress;
    private string _statusText = string.Empty;
    private bool _isGenerating;

    // Project-wide Room Order (ordered room names), loaded from ExtensibleStorage at collection
    // time and passed to LoadCircuits — a VM cannot read Revit synchronously. Drives By Room.
    private List<string> _roomOrder = new();

    public string ProjectName { get; }
    public ObservableCollection<LoadsCircuitModel> Circuits { get; } = new();

    /// <summary>By Circuit (flat list) vs By Room (grouped) export radio. Mirrors the Cut Sheets
    /// package radio; the setter re-queries so the DocsViewModel wiring picks up the flip.</summary>
    public bool IsByRoom
    {
        get => _isByRoom;
        set
        {
            if (SetProperty(ref _isByRoom, value))
            {
                OnPropertyChanged(nameof(IsByCircuit));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsByCircuit
    {
        get => !_isByRoom;
        set => IsByRoom = !value;
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

    public LoadsViewModel(string projectName, DocsViewModel parent)
    {
        _parent = parent;
        ProjectName = projectName;

        GenerateCommand = new RelayCommand(ExecuteGenerate, () => !IsGenerating && Circuits.Count > 0);
    }

    public void LoadCircuits(List<LoadsCircuitModel> circuits, List<string> roomOrder)
    {
        Circuits.Clear();
        foreach (var c in circuits)
            Circuits.Add(c);

        _roomOrder = roomOrder ?? new List<string>();

        var settings = DocsSettingsService.Load();
        _isByRoom = settings.LoadsByRoom;
        OnPropertyChanged(nameof(IsByRoom));
        OnPropertyChanged(nameof(IsByCircuit));
    }

    public void SaveSettings()
    {
        var settings = DocsSettingsService.Load();
        settings.LoadsByRoom = IsByRoom;
        DocsSettingsService.Save(settings);
    }

    /// <summary>By Circuit ordering: natural circuit number, with the DMX "&lt;...&gt;" placeholder
    /// pinned to the bottom regardless — matching the grid.</summary>
    private List<LoadsCircuitModel> GetByCircuitList() =>
        Circuits
            .OrderBy(c => c.CircuitNumber == "<...>" ? 1 : 0)
            .ThenBy(c => c.CircuitNumber, NaturalStringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.LoadName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async void ExecuteGenerate()
    {
        if (Circuits.Count == 0) return;

        bool byRoom = IsByRoom;

        // Distinct default names so a By Circuit and a By Room export can co-exist in one folder.
        var saveDialog = new SaveFileDialog
        {
            Filter = "PDF Files|*.pdf",
            FileName = byRoom
                ? $"{ProjectName} Load Schedule by Room.pdf"
                : $"{ProjectName} Load Schedule.pdf"
        };
        if (saveDialog.ShowDialog() != true) return;

        _parent.SaveSettings();
        IsGenerating = true;
        Progress = 0;

        var byCircuitList = GetByCircuitList();
        var sections = byRoom
            ? LoadScheduleSectioner.Section(Circuits.ToList(), _roomOrder)
            : null;

        try
        {
            StatusText = "Generating load schedule...";
            Progress = 50;

            string outputPath = saveDialog.FileName;
            var settings = new DocsSettings
            {
                LogoFilePath = _parent.LogoFilePath,
                CompanyAddress = _parent.CompanyAddress,
                CompanyPhone = _parent.CompanyPhone,
                CompanyEmail = _parent.CompanyEmail,
                CompanyWebsite = _parent.CompanyWebsite,
                FooterDate = _parent.HeaderDate.ToString("yyyy.MM.dd"),
            };

            if (byRoom)
                await Task.Run(() => LoadsPdfService.GenerateByRoom(sections!, ProjectName, outputPath, settings));
            else
                await Task.Run(() => LoadsPdfService.Generate(byCircuitList, ProjectName, outputPath, settings));

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
