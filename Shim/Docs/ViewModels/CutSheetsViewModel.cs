using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Docs.ViewModels;

public class CutSheetsViewModel : ViewModelBase
{
    private readonly DocsViewModel _parent;
    private double _progress;
    private string _statusText = string.Empty;
    private bool _isGenerating;

    private bool _isFixturePackage = true;

    public string ProjectName { get; }
    public ObservableCollection<FixtureSpecModel> Fixtures { get; }

    /// <summary>Control Package rows (control parts), loaded post-construction via
    /// <see cref="LoadControlRows"/> — mirrors the BomVM.LoadData idiom.</summary>
    public ObservableCollection<FixtureSpecModel> ControlParts { get; } = new();

    /// <summary>Fixture vs. Control package radio (mirrors NotesViewModel). The setter re-queries
    /// CanExecute — the shared window Generate button gates on the current mode's collection, and the
    /// DocsViewModel wiring only re-queries on IsGenerating, not on a mode flip.</summary>
    public bool IsFixturePackage
    {
        get => _isFixturePackage;
        set
        {
            if (SetProperty(ref _isFixturePackage, value))
            {
                OnPropertyChanged(nameof(IsControlPackage));
                OnPropertyChanged(nameof(CurrentRows));
                OnPropertyChanged(nameof(GridGroupHeader));
                OnPropertyChanged(nameof(ItemColumnHeader));
                OnPropertyChanged(nameof(DescriptionColumnHeader));
                OnPropertyChanged(nameof(ItemColumnWidth));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsControlPackage
    {
        get => !_isFixturePackage;
        set => IsFixturePackage = !value;
    }

    /// <summary>The collection the grid binds to — fixtures or control parts, by mode.</summary>
    public ObservableCollection<FixtureSpecModel> CurrentRows => IsFixturePackage ? Fixtures : ControlParts;

    // Mode-bound labels — fixture wording unchanged, control shows generic terms.
    public string GridGroupHeader => IsFixturePackage ? "Fixture Types" : "Control Parts";
    public string ItemColumnHeader => IsFixturePackage ? "Type" : "Item";
    public string DescriptionColumnHeader => IsFixturePackage ? "Family Name" : "Description";

    // Fixture "Type" marks are short (45px, unchanged); control labels (combined part numbers /
    // Model names) need room — long ones trim with a tooltip.
    public System.Windows.Controls.DataGridLength ItemColumnWidth =>
        IsFixturePackage ? new System.Windows.Controls.DataGridLength(45)
                         : new System.Windows.Controls.DataGridLength(160);

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

    public RelayCommand<FixtureSpecModel> BrowseLocalPdfCommand { get; }
    public RelayCommand<FixtureSpecModel> ClearLocalPdfCommand { get; }
    public RelayCommand<FixtureSpecModel> SetDefaultPdfCommand { get; }
    public RelayCommand<FixtureSpecModel> ClearDefaultPdfCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand DeselectAllCommand { get; }
    public RelayCommand GenerateCommand { get; }

    public CutSheetsViewModel(List<FixtureSpecModel> fixtures, string projectName, DocsViewModel parent)
    {
        _parent = parent;
        ProjectName = projectName;

        // Collapse families that share a Type Mark to one row. Tie-break order:
        //   1. Populated DataSheetUrl / CatalogNumber (don't lose data to a blank sibling).
        //   2. "Base name" — variant whose tokens are a subset of every sibling's
        //      (e.g. "Tape" beats "Tape (Hook)", "Tape - Hook", "Bar Tape", "Circular Tape").
        //   3. Shortest family name, then alphabetical.
        var deduped = fixtures
            .GroupBy(f => f.TypeMark)
            .Select(PickPrimary);
        Fixtures = new ObservableCollection<FixtureSpecModel>(deduped);

        var settings = DocsSettingsService.Load();

        foreach (var fixture in Fixtures)
        {
            var key = SettingsKey(fixture);

            // Priority: per-project local path > global default by catalog number
            if (settings.LocalPdfPaths.TryGetValue(key, out var path) && File.Exists(path))
            {
                fixture.LocalPdfPath = path;
            }
            else if (!string.IsNullOrWhiteSpace(fixture.CatalogNumber)
                     && settings.DefaultLocalPdfPaths.TryGetValue(fixture.CatalogNumber, out var defaultPath)
                     && File.Exists(defaultPath))
            {
                fixture.LocalPdfPath = defaultPath;
            }

            // Check if the loaded path matches a global default — show gold star
            if (fixture.HasLocalPdf && !string.IsNullOrWhiteSpace(fixture.CatalogNumber)
                && settings.DefaultLocalPdfPaths.TryGetValue(fixture.CatalogNumber, out var defPath)
                && fixture.LocalPdfPath == defPath)
            {
                fixture.IsDefaultPdf = true;
            }

            if (settings.SelectedTypeMarks.Count > 0)
                fixture.IsSelected = settings.SelectedTypeMarks.Contains(key);
        }

        BrowseLocalPdfCommand = new RelayCommand<FixtureSpecModel>(ExecuteBrowseLocalPdf);
        ClearLocalPdfCommand = new RelayCommand<FixtureSpecModel>(f => { f.LocalPdfPath = string.Empty; f.IsDefaultPdf = false; });
        SetDefaultPdfCommand = new RelayCommand<FixtureSpecModel>(ExecuteSetDefaultPdf);
        ClearDefaultPdfCommand = new RelayCommand<FixtureSpecModel>(ExecuteClearDefaultPdf);
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true));
        DeselectAllCommand = new RelayCommand(() => SetAllSelected(false));
        GenerateCommand = new RelayCommand(ExecuteGenerate, () => !IsGenerating && CurrentRows.Any(f => f.IsSelected));
    }

    /// <summary>Populates the Control Package rows and applies saved control-mode settings (selection,
    /// per-project local path, gold-star default). Called post-construction from DocsCommand, so the
    /// ctor still takes only fixtures. Keys everything off the row's stable key (its CatalogNumber).</summary>
    public void LoadControlRows(IEnumerable<FixtureSpecModel> rows)
    {
        ControlParts.Clear();
        var settings = DocsSettingsService.Load();

        foreach (var row in rows)
        {
            string key = row.CatalogNumber;

            // Priority: per-project local path > global default by key
            if (settings.ControlLocalPdfPaths.TryGetValue(key, out var path) && File.Exists(path))
                row.LocalPdfPath = path;
            else if (!string.IsNullOrWhiteSpace(key)
                     && settings.ControlDefaultPdfPaths.TryGetValue(key, out var defaultPath)
                     && File.Exists(defaultPath))
                row.LocalPdfPath = defaultPath;

            if (row.HasLocalPdf
                && settings.ControlDefaultPdfPaths.TryGetValue(key, out var defPath)
                && row.LocalPdfPath == defPath)
                row.IsDefaultPdf = true;

            if (settings.SelectedControlKeys.Count > 0)
                row.IsSelected = settings.SelectedControlKeys.Contains(key);

            ControlParts.Add(row);
        }

        OnPropertyChanged(nameof(CurrentRows));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    public void SaveSettings()
    {
        var settings = DocsSettingsService.Load();
        settings.LocalPdfPaths = Fixtures
            .Where(f => f.HasLocalPdf)
            .GroupBy(SettingsKey)
            .ToDictionary(g => g.Key, g => g.First().LocalPdfPath);
        settings.SelectedTypeMarks = Fixtures
            .Where(f => f.IsSelected)
            .Select(SettingsKey)
            .Distinct()
            .ToList();

        // Control mode — persisted in its own fields so the two modes can't clobber each other.
        // Written every save regardless of the active mode (the VM always holds both collections).
        settings.ControlLocalPdfPaths = ControlParts
            .Where(r => r.HasLocalPdf && !string.IsNullOrWhiteSpace(r.CatalogNumber))
            .GroupBy(r => r.CatalogNumber)
            .ToDictionary(g => g.Key, g => g.First().LocalPdfPath);
        settings.SelectedControlKeys = ControlParts
            .Where(r => r.IsSelected)
            .Select(r => r.CatalogNumber)
            .Distinct()
            .ToList();

        DocsSettingsService.Save(settings);
    }

    // Composite key keeps multiple families that share a Type Mark
    // (e.g. "Tape" / "Tape (Arc)" / "Tape (Hook)") from sharing settings rows.
    private static string SettingsKey(FixtureSpecModel f) => $"{f.FamilyName}|{f.TypeMark}";

    private static FixtureSpecModel PickPrimary(IEnumerable<FixtureSpecModel> group)
    {
        var rows = group.ToList();
        var tokens = rows.ToDictionary(r => r, r => Tokenize(r.FamilyName));

        // A row is a "base name" if its tokens appear in every sibling's tokens.
        bool IsBase(FixtureSpecModel r) =>
            tokens[r].Count > 0 && rows.All(s => s == r || tokens[r].IsSubsetOf(tokens[s]));

        return rows
            .OrderByDescending(f => !string.IsNullOrWhiteSpace(f.DataSheetUrl))
            .ThenByDescending(f => !string.IsNullOrWhiteSpace(f.CatalogNumber))
            .ThenByDescending(IsBase)
            .ThenBy(f => f.FamilyName.Length)
            .ThenBy(f => f.FamilyName, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static readonly char[] NameSeparators = [' ', '\t', '(', ')', '-', '/', '_', ','];

    private static HashSet<string> Tokenize(string name) =>
        new(name.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);

    private void ExecuteBrowseLocalPdf(FixtureSpecModel fixture)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PDF Files|*.pdf",
            Title = $"Select Local PDF for {fixture.TypeMark}"
        };
        if (dialog.ShowDialog() == true)
        {
            fixture.LocalPdfPath = dialog.FileName;
            fixture.IsDefaultPdf = false;
        }
    }

    private void ExecuteSetDefaultPdf(FixtureSpecModel fixture)
    {
        if (!fixture.HasLocalPdf || string.IsNullOrWhiteSpace(fixture.CatalogNumber)) return;
        var settings = DocsSettingsService.Load();
        if (IsControlPackage) settings.ControlDefaultPdfPaths[fixture.CatalogNumber] = fixture.LocalPdfPath;
        else settings.DefaultLocalPdfPaths[fixture.CatalogNumber] = fixture.LocalPdfPath;
        DocsSettingsService.Save(settings);
        fixture.IsDefaultPdf = true;
    }

    private void ExecuteClearDefaultPdf(FixtureSpecModel fixture)
    {
        if (string.IsNullOrWhiteSpace(fixture.CatalogNumber)) return;
        var settings = DocsSettingsService.Load();
        if (IsControlPackage) settings.ControlDefaultPdfPaths.Remove(fixture.CatalogNumber);
        else settings.DefaultLocalPdfPaths.Remove(fixture.CatalogNumber);
        DocsSettingsService.Save(settings);
        fixture.IsDefaultPdf = false;
    }

    private void SetAllSelected(bool selected)
    {
        foreach (var row in CurrentRows)
            row.IsSelected = selected;
    }

    private async void ExecuteGenerate()
    {
        if (IsControlPackage) await GenerateControlAsync();
        else await GenerateFixturesAsync();
    }

    private async Task GenerateFixturesAsync()
    {
        var selected = Fixtures.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0) return;

        var saveDialog = new SaveFileDialog
        {
            Filter = "PDF Files|*.pdf",
            FileName = $"{ProjectName} Cut Sheets.pdf"
        };
        if (saveDialog.ShowDialog() != true) return;

        _parent.SaveSettings();
        IsGenerating = true;
        Progress = 0;

        var results = new List<(string typeMark, byte[]? pdfData, string catalogNumber, string dataSheetUrl)>();
        var errors = new List<string>();

        try
        {
            // Load/download phase (0-80%)
            for (int i = 0; i < selected.Count; i++)
            {
                var fixture = selected[i];
                Progress = (double)i / selected.Count * 80.0;

                byte[]? data;
                if (fixture.HasLocalPdf && File.Exists(fixture.LocalPdfPath))
                {
                    StatusText = $"Loading {i + 1} of {selected.Count}: {fixture.TypeMark}...";
                    data = await DownloadService.ReadLocalPdfAsync(fixture.LocalPdfPath);
                    if (data == null) errors.Add(fixture.TypeMark);
                }
                else if (!string.IsNullOrWhiteSpace(fixture.DataSheetUrl))
                {
                    StatusText = $"Downloading {i + 1} of {selected.Count}: {fixture.TypeMark}...";
                    data = await DownloadService.DownloadPdfAsync(fixture.DataSheetUrl, CancellationToken.None);
                    if (data == null) errors.Add(fixture.TypeMark);
                }
                else
                {
                    data = null;
                }

                results.Add((fixture.TypeMark, data, fixture.CatalogNumber, fixture.DataSheetUrl));
            }

            // Merge phase (80-100%)
            StatusText = "Merging PDFs...";
            Progress = 85;

            var settings = new DocsSettings
            {
                LogoFilePath = _parent.LogoFilePath,
                CompanyAddress = _parent.CompanyAddress,
                CompanyPhone = _parent.CompanyPhone,
                CompanyEmail = _parent.CompanyEmail,
                CompanyWebsite = _parent.CompanyWebsite,
                HeaderDate = _parent.HeaderDate.ToString("MMM dd, yyyy")
            };

            string outputPath = saveDialog.FileName;
            await Task.Run(() => CutSheetPdfService.MergeAndStamp(results, settings, ProjectName, outputPath));

            Progress = 100;
            StatusText = errors.Count > 0
                ? $"Done. {errors.Count} failed: {string.Join(", ", errors)}. Saved to {Path.GetFileName(outputPath)}"
                : $"Done. Saved to {Path.GetFileName(outputPath)}";
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

    private async Task GenerateControlAsync()
    {
        // Only selected rows that actually have something to bind (a URL or an attached local PDF).
        // "No cutsheet" rows are shown/selectable in the grid but contribute no page.
        var selected = ControlParts
            .Where(r => r.IsSelected
                        && (!string.IsNullOrWhiteSpace(r.DataSheetUrl)
                            || (r.HasLocalPdf && File.Exists(r.LocalPdfPath))))
            .ToList();
        if (selected.Count == 0)
        {
            StatusText = "Nothing to generate — no selected control part has a cutsheet.";
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Filter = "PDF Files|*.pdf",
            FileName = $"{ProjectName} Control Cut Sheets.pdf"
        };
        if (saveDialog.ShowDialog() != true) return;

        _parent.SaveSettings();
        IsGenerating = true;
        Progress = 0;

        var results = new List<(string typeMark, byte[]? pdfData, string catalogNumber, string dataSheetUrl)>();
        var errors = new List<string>();

        try
        {
            // Load/download phase (0-80%)
            for (int i = 0; i < selected.Count; i++)
            {
                var row = selected[i];
                Progress = (double)i / selected.Count * 80.0;

                byte[]? data;
                if (row.HasLocalPdf && File.Exists(row.LocalPdfPath))
                {
                    StatusText = $"Loading {i + 1} of {selected.Count}: {row.TypeMark}...";
                    data = await DownloadService.ReadLocalPdfAsync(row.LocalPdfPath);
                }
                else
                {
                    StatusText = $"Downloading {i + 1} of {selected.Count}: {row.TypeMark}...";
                    data = await DownloadService.DownloadPdfAsync(row.DataSheetUrl, CancellationToken.None);
                }
                if (data == null) errors.Add(row.TypeMark);

                results.Add((row.TypeMark, data, row.CatalogNumber, row.DataSheetUrl));
            }

            // Merge phase (80-100%) — plain bind, no header/footer stamp
            StatusText = "Merging PDFs...";
            Progress = 85;

            string outputPath = saveDialog.FileName;
            await Task.Run(() => CutSheetPdfService.MergePlain(results, ProjectName, outputPath));

            Progress = 100;
            StatusText = errors.Count > 0
                ? $"Done. {errors.Count} failed: {string.Join(", ", errors)}. Saved to {Path.GetFileName(outputPath)}"
                : $"Done. Saved to {Path.GetFileName(outputPath)}";
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
