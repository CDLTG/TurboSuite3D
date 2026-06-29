#nullable enable
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Dmx.ViewModels
{
    /// <summary>One unassigned Control Zone sitting in the pool — the loop-level residual (the "(unassigned)"
    /// bucket, symmetric with the cluster "(unclustered)" residual). Multi-select source for the assignment
    /// gesture: tick zones here, then a loop's "+ Add" (or "+ New loop from selection") moves them into that
    /// loop. A pooled zone is still solved — the engine auto-packs it; declaring a loop is the geometry
    /// override on top.</summary>
    public sealed class DmxZonePoolItemViewModel : ViewModelBase
    {
        private bool _isSelected;

        public DmxZonePoolItemViewModel(string zoneName, int runCount)
        {
            ZoneName = zoneName;
            RunCount = runCount;
        }

        public string ZoneName { get; }
        public int RunCount { get; }

        /// <summary>Bound to the pool ListBoxItem's IsSelected — the multi-select assignment source.</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public string Display => $"{ZoneName}  ({RunCount})";
    }

    /// <summary>Per-loop placement state, derived from the reconciled numbering + the persisted placement
    /// registry (no model scan): is every DEC # this loop owns already in the model?</summary>
    public enum DmxLoopPlacementState { Unsolved, Unplaced, Partial, Placed }

    /// <summary>
    /// A designer-declared DMX Loop as a tree node (the loop-centric window): a name plus the Control Zones
    /// assigned to it (each a <see cref="DmxLoopZoneViewModel"/> carrying its own cluster sub-builder).
    /// Maps to an engine <see cref="LoopDeclaration"/>; assigning more channels than the interface ceiling is
    /// the engine's third pre-solve gate, surfaced on Run. Carries its own per-loop Place action + placement
    /// state (the loop is the placement unit — one pick lands just this loop's decoders + drivers).
    /// </summary>
    public sealed class DmxLoopRowViewModel : ViewModelBase
    {
        private string _name;
        private DmxLoopPlacementState _placementState = DmxLoopPlacementState.Unsolved;

        public DmxLoopRowViewModel(string name)
        {
            _name = name;
            Zones = new ObservableCollection<DmxLoopZoneViewModel>();
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>The zones assigned to this loop, in chain order, each with its nested cluster sub-builder.</summary>
        public ObservableCollection<DmxLoopZoneViewModel> Zones { get; }

        /// <summary>The assigned zone names, in list order — the loop's chain order.</summary>
        public IReadOnlyList<string> AssignedZoneNames =>
            Zones.Select(z => z.ZoneName).ToList();

        /// <summary>Convert to the engine declaration (null if the loop has no zones — skip it).</summary>
        public LoopDeclaration? ToDeclaration()
        {
            var zones = AssignedZoneNames;
            return zones.Count == 0 ? null : new LoopDeclaration(Name, zones);
        }

        /// <summary>The interface # the last solve assigned this loop (0 = not yet solved / empty). Set by the
        /// ViewModel after each Run; the per-loop Place targets exactly this interface.</summary>
        public int InterfaceNumber { get; set; }

        public DmxLoopPlacementState PlacementState
        {
            get => _placementState;
            set
            {
                if (SetProperty(ref _placementState, value))
                {
                    OnPropertyChanged(nameof(StateGlyph));
                    OnPropertyChanged(nameof(StateText));
                }
            }
        }

        public string StateGlyph => _placementState switch
        {
            DmxLoopPlacementState.Placed => "●",
            DmxLoopPlacementState.Partial => "◑",
            DmxLoopPlacementState.Unplaced => "○",
            _ => "–",
        };

        public string StateText => _placementState switch
        {
            DmxLoopPlacementState.Placed => "placed",
            DmxLoopPlacementState.Partial => "partial",
            DmxLoopPlacementState.Unplaced => "unplaced",
            _ => "—",
        };

        // Wired by the owning ViewModel (it holds the pool selection + the solve/registry the actions need).
        public ICommand? AddSelectedCommand { get; set; }   // + Add selected pool zones to this loop
        public ICommand? RemoveCommand { get; set; }        // ✕ delete loop (its zones return to the pool)
        public ICommand? PlaceCommand { get; set; }         // Place ▸ this loop's decoders + drivers
        public ICommand? DrawOneLineCommand { get; set; }   // One-line ▸ draw/redraw this loop's diagram (Phase 4)
    }
}
