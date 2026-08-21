#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Dali.ViewModels
{
    /// <summary>One Control Zone value in TurboDALI's loop pool — either sitting unassigned or as a
    /// member of a loop. Carries its DALI load count (one addressable load per DALI fixture in that zone) so
    /// a loop can sum its legs, and an <see cref="IsSelected"/> flag bound to the pool ListBox for the
    /// multi-select "add to loop" gesture (the scaled-down DMX pool pattern).</summary>
    public sealed class DaliZoneItemViewModel : ViewModelBase
    {
        private bool _isSelected;

        public DaliZoneItemViewModel(string zoneName, int loadCount)
        {
            ZoneName = zoneName;
            LoadCount = loadCount;
        }

        public string ZoneName { get; }
        public int LoadCount { get; }

        /// <summary>Bound to the pool ListBoxItem's IsSelected — the multi-select assignment source.</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public string Display => $"{ZoneName}  ({LoadCount})";
    }

    /// <summary>A ZONE N choice in a loop's assignment dropdown. Value 0 is the "&lt;Unassigned&gt;" sentinel;
    /// any positive value is a panel ZONE N discovered in the job.</summary>
    public sealed class DaliZoneOption
    {
        public DaliZoneOption(int value, string label)
        {
            Value = value;
            Label = label;
        }

        public int Value { get; }
        public string Label { get; }
    }

    /// <summary>
    /// A designer-declared DALI loop as an editable row: a name, the Control Zones grouped onto its one DALI
    /// bus, and the required ZONE N its <c>LQSE2-1DALUNV-D</c> module is placed in. Deliberately flat — no
    /// nested cluster sub-builder like the DMX loop row — because this tab is a transitional home and the
    /// solve/one-line surface belongs to a future TurboDALI.
    ///
    /// The load count is the sum of its member zones' loads (one leg each); over 64 the loop can't fit on
    /// one bus and is flagged (a warning, never an auto-split). Unassigned (<see cref="AssignedZone"/> 0)
    /// with loads means the module is ordered job-wide but has no panel to sit in — also flagged.
    /// </summary>
    public sealed class DaliLoopRowViewModel : ViewModelBase
    {
        /// <summary>DALI-addressable loads a single bus (one module) carries. Mirrors DaliSolver.MaxLoadsPerBus;
        /// duplicated here to keep this VM free of a Services dependency (it is the display-side of the rule).</summary>
        public const int MaxLoadsPerBus = 64;

        private string _name;
        private int _assignedZone;
        private bool _isZonesExpanded = true;

        public DaliLoopRowViewModel(string loopId, string name, int assignedZone,
                                    IReadOnlyList<DaliZoneOption> zoneOptions)
        {
            LoopId = loopId;
            _name = name;
            _assignedZone = assignedZone;
            ZoneOptions = zoneOptions;
            Zones = new ObservableCollection<DaliZoneItemViewModel>();
            Zones.CollectionChanged += OnZonesChanged;
        }

        public string LoopId { get; }

        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                    Changed?.Invoke();
            }
        }

        /// <summary>The Control Zones on this loop's bus, in declared order.</summary>
        public ObservableCollection<DaliZoneItemViewModel> Zones { get; }

        /// <summary>The collapse header for the member-zone list, e.g. "Control Zones (3)".</summary>
        public string ZonesHeader => $"Control Zones ({Zones.Count})";

        /// <summary>Whether the member-zone list is expanded — pure view state (not persisted). Starts open;
        /// lets the designer collapse a long zone list on a crowded loops column. Mirrors no DMX control —
        /// TurboDALI can carry more zones per loop, so its list earns a collapse toggle.</summary>
        public bool IsZonesExpanded
        {
            get => _isZonesExpanded;
            set => SetProperty(ref _isZonesExpanded, value);
        }

        /// <summary>The ZONE N options for the assignment dropdown (0 = unassigned, then each discovered zone).</summary>
        public IReadOnlyList<DaliZoneOption> ZoneOptions { get; }

        /// <summary>The ZONE N this loop's module is placed in; 0 = unassigned (ordered, not placed).</summary>
        public int AssignedZone
        {
            get => _assignedZone;
            set
            {
                if (SetProperty(ref _assignedZone, value))
                {
                    OnPropertyChanged(nameof(IsUnassigned));
                    OnPropertyChanged(nameof(StatusText));
                    Changed?.Invoke();
                }
            }
        }

        public int LoadCount => Zones.Sum(z => z.LoadCount);

        /// <summary>Over the one-bus cap — can't fit on its single module; the fix is to split its zones.</summary>
        public bool IsOverCap => LoadCount > MaxLoadsPerBus;

        /// <summary>Has loads but no ZONE N — ordered by the job-wide demand, placed nowhere.</summary>
        public bool IsUnassigned => LoadCount > 0 && AssignedZone <= 0;

        /// <summary>A <c>used/64</c> bus meter (a loop = one DALI bus). Zero-padded to two digits to match the
        /// address short-address slots (00–63), so the 64-cap is legible at a glance instead of hiding behind
        /// an ambiguous "loads" word — this is the surface where the per-unit count fix becomes visible.</summary>
        public string StatusText
        {
            get
            {
                if (Zones.Count == 0) return "empty";
                string meter = $"{LoadCount:00}/{MaxLoadsPerBus}";
                if (IsOverCap) return $"{meter} — over bus limit, split this loop";
                if (IsUnassigned) return $"{meter} — no zone, not placed";
                return meter;
            }
        }

        /// <summary>Tooltip for the status meter — spells out what the <c>used/64</c> ratio means.</summary>
        public string StatusTooltip => "DALI addresses used / bus capacity";

        /// <summary>Raised on any edit the owning tab must react to (recompute aggregates + persist).</summary>
        public Action? Changed { get; set; }

        // Wired by the owning ViewModel (it holds the pool selection the add gesture needs).
        public ICommand? AddSelectedCommand { get; set; }   // + move selected pool zones into this loop
        public ICommand? RemoveCommand { get; set; }         // ✕ delete loop (its zones return to the pool)
        public ICommand? RemoveZoneCommand { get; set; }     // ✕ remove one zone (it returns to the pool)

        private void OnZonesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(LoadCount));
            OnPropertyChanged(nameof(IsOverCap));
            OnPropertyChanged(nameof(IsUnassigned));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ZonesHeader));

            // A drag-to-reorder is a Move: no load-count change, so the aggregate/persist path the Add/Remove
            // callers drive explicitly would never fire. Persist it here — the zones' declared order IS the
            // outer addressing key, so reordering must reach ExtensibleStorage (the owning tab's Changed handler
            // recomputes + saves, and is _loaded-guarded so the load-time member fill stays silent).
            if (e.Action == NotifyCollectionChangedAction.Move)
                Changed?.Invoke();
        }
    }
}
