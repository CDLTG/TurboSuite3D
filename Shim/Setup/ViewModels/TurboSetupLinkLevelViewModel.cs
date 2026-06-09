#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Autodesk.Revit.DB;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Setup.ViewModels;

/// <summary>
/// Stage 1: pick the architectural link and the levels to set up, and mark one Main level.
/// Runs on the Revit thread (modal dialog), so it holds Revit objects directly.
/// </summary>
public class TurboSetupLinkLevelViewModel : ViewModelBase
{
    /// <summary>One selectable architectural link.</summary>
    public class LinkOption
    {
        public LinkOption(ElementId instanceId, string displayName, Document linkDoc)
        {
            InstanceId = instanceId;
            DisplayName = displayName;
            LinkDocument = linkDoc;
        }

        public ElementId InstanceId { get; }
        public string DisplayName { get; }
        public Document LinkDocument { get; }
        public override string ToString() => DisplayName;
    }

    private LinkOption _selectedLink;

    public ObservableCollection<LinkOption> Links { get; } = new ObservableCollection<LinkOption>();

    public ObservableCollection<LevelRowViewModel> Levels { get; } = new ObservableCollection<LevelRowViewModel>();

    /// <summary>True once the user clicks OK with a valid selection.</summary>
    public bool Confirmed { get; private set; }

    public ICommand OkCommand { get; }

    public event Action CloseRequested;

    public TurboSetupLinkLevelViewModel(IEnumerable<LinkOption> links)
    {
        foreach (var link in links)
            Links.Add(link);

        OkCommand = new RelayCommand(ExecuteOk, CanOk);

        if (Links.Count > 0)
            SelectedLink = Links[0];
    }

    public LinkOption SelectedLink
    {
        get => _selectedLink;
        set
        {
            if (SetProperty(ref _selectedLink, value))
                LoadLevels();
        }
    }

    private void LoadLevels()
    {
        Levels.Clear();
        if (SelectedLink?.LinkDocument == null)
            return;

        var levels = new FilteredElementCollector(SelectedLink.LinkDocument)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .ToList();

        foreach (var level in levels)
        {
            var row = new LevelRowViewModel(level.Id, level.Name, level.Elevation);
            row.MainRequested += OnMainRequested;
            Levels.Add(row);
        }

        ApplyDefaultMain();
    }

    // Default Main = lowest above-grade (elevation >= 0) included level; if none above grade,
    // the highest included level. Seeds the radio only — the user can override.
    private void ApplyDefaultMain()
    {
        var included = Levels.Where(l => l.IsIncluded).ToList();
        if (included.Count == 0)
            return;

        var aboveGrade = included.Where(l => l.Elevation >= 0).OrderBy(l => l.Elevation).ToList();
        var main = aboveGrade.Count > 0 ? aboveGrade.First() : included.OrderBy(l => l.Elevation).Last();
        main.IsMain = true;
    }

    private void OnMainRequested(LevelRowViewModel chosen)
    {
        foreach (var row in Levels)
            if (!ReferenceEquals(row, chosen))
                row.ClearMainSilently();
    }

    private bool CanOk() => Levels.Any(l => l.IsIncluded);

    private void ExecuteOk()
    {
        Confirmed = true;
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// The included levels, sorted by elevation, with the chosen Main resolved. If no row is
    /// marked Main, the single included level (or the default rule) applies.
    /// </summary>
    public IReadOnlyList<LevelRowViewModel> GetSelectedLevels(out int mainIndex)
    {
        var selected = Levels.Where(l => l.IsIncluded).OrderBy(l => l.Elevation).ToList();

        int idx = selected.FindIndex(l => l.IsMain);
        if (idx < 0)
        {
            // No explicit Main among the included set — fall back to the default rule.
            var aboveGrade = selected.Where(l => l.Elevation >= 0).ToList();
            var fallback = aboveGrade.Count > 0 ? aboveGrade.First() : selected.LastOrDefault();
            idx = fallback != null ? selected.IndexOf(fallback) : 0;
        }

        mainIndex = selected.Count == 0 ? 0 : idx;
        return selected;
    }
}
