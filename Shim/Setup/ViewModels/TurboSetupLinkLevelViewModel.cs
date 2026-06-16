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
            row.PropertyChanged += OnLevelRowChanged;
            Levels.Add(row);
        }

        // No auto-seeded Main. Elevation can't recover which level is "Main" — arch models set
        // levels to real-world/survey elevations, so an "above grade = elevation >= 0" rule just
        // lands on the lowest/highest level in the stack, not the main floor. Forcing a conscious
        // pick (the radio gates Next) is correct rather than seeding a misleading default.
        OnPropertyChanged(nameof(ShowMainHint));
    }

    private void OnLevelRowChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LevelRowViewModel.IsIncluded) ||
            e.PropertyName == nameof(LevelRowViewModel.IsMain))
            OnPropertyChanged(nameof(ShowMainHint));
    }

    private void OnMainRequested(LevelRowViewModel chosen)
    {
        foreach (var row in Levels)
            if (!ReferenceEquals(row, chosen))
                row.ClearMainSilently();
    }

    /// <summary>
    /// Shown beneath the grid when levels are included but none of the included ones is marked
    /// Main — i.e. Next is blocked specifically on the Main pick. Hidden once a Main is chosen.
    /// </summary>
    public bool ShowMainHint =>
        Levels.Any(l => l.IsIncluded) && !Levels.Any(l => l.IsIncluded && l.IsMain);

    // Next requires at least one included level AND a Main that is itself included.
    private bool CanOk() => Levels.Any(l => l.IsIncluded && l.IsMain);

    private void ExecuteOk()
    {
        Confirmed = true;
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// The included levels, sorted by elevation, with the chosen Main resolved. Confirmation is
    /// gated on an included Main (see <see cref="CanOk"/>), so the Main is always present here.
    /// </summary>
    public IReadOnlyList<LevelRowViewModel> GetSelectedLevels(out int mainIndex)
    {
        var selected = Levels.Where(l => l.IsIncluded).OrderBy(l => l.Elevation).ToList();
        mainIndex = System.Math.Max(0, selected.FindIndex(l => l.IsMain));
        return selected;
    }
}
