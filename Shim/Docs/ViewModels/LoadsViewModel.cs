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

/// <summary>The three mutually-exclusive Load Schedule outputs. Page size is not an orthogonal
/// axis here — the 28.5" construction strip only exists for By Circuit — so it collapses into a
/// single radio group rather than a separate Page Size row (contrast the Power Supplies tab,
/// where Large applies to all outputs and stays orthogonal).</summary>
public enum LoadsFormat { ByCircuit, ByCircuitConstruction, ByRoom }

public class LoadsViewModel : ViewModelBase
{
    private readonly DocsViewModel _parent;
    private LoadsFormat _format = LoadsFormat.ByCircuit;
    private double _progress;
    private string _statusText = string.Empty;
    private bool _isGenerating;

    // Project-wide Room Order (ordered room names), loaded from ExtensibleStorage at collection
    // time and passed to LoadCircuits — a VM cannot read Revit synchronously. Drives By Room.
    private List<string> _roomOrder = new();

    public string ProjectName { get; }
    public ObservableCollection<LoadsCircuitModel> Circuits { get; } = new();

    /// <summary>Backing field for the three Format radios. Each radio binds one of the IsXxx
    /// bool properties; their setters only act on a true (a click that selects) and ignore the
    /// false the group raises on the deselected radio — the enum is the single source of truth.</summary>
    private LoadsFormat Format
    {
        get => _format;
        set
        {
            if (SetProperty(ref _format, value))
            {
                OnPropertyChanged(nameof(IsByCircuit));
                OnPropertyChanged(nameof(IsByCircuitConstruction));
                OnPropertyChanged(nameof(IsByRoom));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsByCircuit
    {
        get => _format == LoadsFormat.ByCircuit;
        set { if (value) Format = LoadsFormat.ByCircuit; }
    }

    public bool IsByCircuitConstruction
    {
        get => _format == LoadsFormat.ByCircuitConstruction;
        set { if (value) Format = LoadsFormat.ByCircuitConstruction; }
    }

    public bool IsByRoom
    {
        get => _format == LoadsFormat.ByRoom;
        set { if (value) Format = LoadsFormat.ByRoom; }
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
        // ByRoom wins; otherwise the stored page-size flag picks Letter vs Construction.
        _format = settings.LoadsByRoom
            ? LoadsFormat.ByRoom
            : settings.LoadsUseLargeFormat ? LoadsFormat.ByCircuitConstruction : LoadsFormat.ByCircuit;
        OnPropertyChanged(nameof(IsByCircuit));
        OnPropertyChanged(nameof(IsByCircuitConstruction));
        OnPropertyChanged(nameof(IsByRoom));
    }

    public void SaveSettings()
    {
        var settings = DocsSettingsService.Load();
        // Never write the invalid (ByRoom && Large) pair — the three states map cleanly onto
        // the two flags, so By Circuit ↔ Construction round-trips even after a By Room detour.
        settings.LoadsByRoom = _format == LoadsFormat.ByRoom;
        settings.LoadsUseLargeFormat = _format == LoadsFormat.ByCircuitConstruction;
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

        LoadsFormat format = _format;

        // Distinct default names so the three exports can co-exist in one folder.
        var saveDialog = new SaveFileDialog
        {
            Filter = "PDF Files|*.pdf",
            FileName = format switch
            {
                LoadsFormat.ByRoom => $"{ProjectName} Load Schedule by Room.pdf",
                LoadsFormat.ByCircuitConstruction => $"{ProjectName} Load Schedule (Construction).pdf",
                _ => $"{ProjectName} Load Schedule.pdf",
            }
        };
        if (saveDialog.ShowDialog() != true) return;

        _parent.SaveSettings();
        IsGenerating = true;
        Progress = 0;

        var byCircuitList = GetByCircuitList();
        var sections = format == LoadsFormat.ByRoom
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

            await Task.Run(() =>
            {
                switch (format)
                {
                    case LoadsFormat.ByRoom:
                        LoadsPdfService.GenerateByRoom(sections!, ProjectName, outputPath, settings);
                        break;
                    case LoadsFormat.ByCircuitConstruction:
                        LoadsPdfService.GenerateByCircuitConstruction(byCircuitList, ProjectName, outputPath, settings);
                        break;
                    default:
                        LoadsPdfService.Generate(byCircuitList, ProjectName, outputPath, settings);
                        break;
                }
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
