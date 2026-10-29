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

/// <summary>The four mutually-exclusive Load Schedule outputs. Page size is not an orthogonal
/// axis here — the 28.5" construction strip only exists for By Circuit — so it collapses into a
/// single radio group rather than a separate Page Size row (contrast the Power Supplies tab,
/// where Large applies to all outputs and stays orthogonal). <see cref="ExcelByCircuit"/> is the
/// By Circuit list written to .xlsx instead of PDF.</summary>
public enum LoadsFormat { ByCircuit, ByCircuitConstruction, ByRoom, ExcelByCircuit }

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

    /// <summary>Backing field for the four Format radios. Each radio binds one of the IsXxx
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
                OnPropertyChanged(nameof(IsExcelByCircuit));
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

    public bool IsExcelByCircuit
    {
        get => _format == LoadsFormat.ExcelByCircuit;
        set { if (value) Format = LoadsFormat.ExcelByCircuit; }
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
        // Excel wins, then ByRoom; otherwise the stored page-size flag picks Letter vs Construction.
        _format = settings.LoadsExportExcel ? LoadsFormat.ExcelByCircuit
            : settings.LoadsByRoom ? LoadsFormat.ByRoom
            : settings.LoadsUseLargeFormat ? LoadsFormat.ByCircuitConstruction
            : LoadsFormat.ByCircuit;
        OnPropertyChanged(nameof(IsByCircuit));
        OnPropertyChanged(nameof(IsByCircuitConstruction));
        OnPropertyChanged(nameof(IsByRoom));
        OnPropertyChanged(nameof(IsExcelByCircuit));
    }

    public void SaveSettings()
    {
        var settings = DocsSettingsService.Load();
        // At most one flag is ever written true — the four states map cleanly onto the three
        // flags (all false = By Circuit), so every radio round-trips on its own.
        settings.LoadsByRoom = _format == LoadsFormat.ByRoom;
        settings.LoadsUseLargeFormat = _format == LoadsFormat.ByCircuitConstruction;
        settings.LoadsExportExcel = _format == LoadsFormat.ExcelByCircuit;
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

        bool isExcel = format == LoadsFormat.ExcelByCircuit;

        // Distinct default names so the exports can co-exist in one folder.
        var saveDialog = new SaveFileDialog
        {
            Filter = isExcel ? "Excel Files|*.xlsx" : "PDF Files|*.pdf",
            FileName = format switch
            {
                LoadsFormat.ExcelByCircuit => $"{ProjectName} Load Schedule.xlsx",
                LoadsFormat.ByRoom => $"{ProjectName} Load Schedule by Room.pdf",
                LoadsFormat.ByCircuitConstruction => $"{ProjectName} Load Schedule (Construction).pdf",
                _ => $"{ProjectName} Load Schedule.pdf",
            }
        };
        if (saveDialog.ShowDialog() != true) return;

        // Re-exporting over a workbook still open in Excel fails on save with an opaque
        // IOException — catch it up front and say what to do.
        if (isExcel && FileLockHelper.IsFileLocked(saveDialog.FileName))
        {
            StatusText = $"{Path.GetFileName(saveDialog.FileName)} is open. Close it in Excel, then try again.";
            return;
        }

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
                    case LoadsFormat.ExcelByCircuit:
                        LoadsWorkbookService.Generate(byCircuitList, ProjectName, outputPath);
                        break;
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
