#nullable disable
using System.Collections.ObjectModel;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Snoop.ViewModels;

/// <summary>
/// DataContext for the modeless TurboSnoop window — a thin, Revit-free holder for the picked family's report
/// tree. Pure display: the pick already happened in the command, so there is no command surface here. The
/// window is read-only by design — see the SnoopCommand "FINDER, NOT A HIDER" header.
/// </summary>
public sealed class SnoopMainViewModel : ViewModelBase
{
    public SnoopMainViewModel(string header, SnoopNodeViewModel root)
    {
        Header = header;
        if (root != null)
            Roots.Add(root);
    }

    /// <summary>The picked family's "FamilyName : Type" label, shown above the tree (the tree's root is the
    /// family's category, matching the VG menu).</summary>
    public string Header { get; }

    /// <summary>The report tree (a single root = the family's category).</summary>
    public ObservableCollection<SnoopNodeViewModel> Roots { get; } = new();
}
