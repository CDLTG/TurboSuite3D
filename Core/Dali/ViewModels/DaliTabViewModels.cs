#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using TurboSuite.Dali.Addressing;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Dali.ViewModels
{
    /// <summary>One circuit as fed into a zone's sort: the durable <see cref="CircuitKey"/> + its display
    /// <see cref="Label"/> (cosmetic) + the geometry (<see cref="Room"/>/<see cref="Centroid"/>) the Auto sort
    /// needs. Built shim-side from the unit readings and carried on <c>DaliTabInputs.CircuitsByZone</c>; the
    /// zone VM keeps it for the life of the window so Reset / a room-order change can re-sort with no model
    /// re-read.</summary>
    public sealed class DaliCircuitSeed
    {
        public DaliCircuitSeed(string circuitKey, string label, string? room, DaliPoint? centroid)
        {
            CircuitKey = circuitKey ?? "";
            Label = label ?? "";
            Room = (room ?? "").Trim();
            Centroid = centroid;
        }

        public string CircuitKey { get; }
        public string Label { get; }
        public string Room { get; }
        public DaliPoint? Centroid { get; }

        public ZoneCircuit ToZoneCircuit() => new ZoneCircuit(CircuitKey, Room, Centroid);
    }

    /// <summary>One draggable circuit row inside a zone's sort list — label-only (the geometry lives on the
    /// owning <see cref="DaliZoneItemViewModel"/>). Dragging reorders these; the durable key is the circuit
    /// UniqueId, so a cosmetic-label collision is harmless.</summary>
    public sealed class DaliCircuitRowViewModel : ViewModelBase
    {
        public DaliCircuitRowViewModel(string circuitKey, string label)
        {
            CircuitKey = circuitKey;
            Label = label;
        }

        public string CircuitKey { get; }
        public string Label { get; }
    }

    /// <summary>One Control Zone value in TurboDALI's loop pool — either sitting unassigned or as a
    /// member of a loop. Carries its DALI load count (one addressable load per DALI fixture in that zone) so
    /// a loop can sum its legs, and an <see cref="IsSelected"/> flag bound to the pool ListBox for the
    /// multi-select "add to loop" gesture (the scaled-down DMX pool pattern).
    ///
    /// <para>When the zone sits in a loop it also carries the <b>within-zone circuit sort</b>: the held
    /// <see cref="ZoneCircuits"/> geometry, the ordered draggable <see cref="Circuits"/> rows, and the
    /// Auto/Manual state. <see cref="ApplyOrder"/> (re)builds the rows from <see cref="DaliZoneOrderer"/>; a
    /// drag <see cref="NotifyCollectionChangedAction.Move"/> flips the zone to Manual + persists; Reset returns
    /// it to Auto. Pool zones carry the same members unused (the shared-VM decision).</para></summary>
    public sealed class DaliZoneItemViewModel : ViewModelBase
    {
        private bool _isSelected;
        private bool _isManual;
        private bool _isExpanded;
        private bool _applying;   // suppress the Move→Manual flip while ApplyOrder rebuilds the rows
        private IReadOnlyDictionary<string, int> _roomRank =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public DaliZoneItemViewModel(string zoneName, int loadCount,
                                     IReadOnlyList<DaliCircuitSeed>? circuits = null)
        {
            ZoneName = zoneName;
            LoadCount = loadCount;

            Seeds = circuits ?? Array.Empty<DaliCircuitSeed>();
            ZoneCircuits = Seeds.Select(s => s.ToZoneCircuit()).ToList();
            Circuits = new ObservableCollection<DaliCircuitRowViewModel>();
            Circuits.CollectionChanged += OnCircuitsChanged;
            ResetSortCommand = new RelayCommand(ResetSort, () => _isManual);
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

        // ── Within-zone circuit sort ────────────────────────────────────────────────────────────────────

        /// <summary>The circuit seeds (label + geometry), held for the window's life so Reset / a room-order
        /// change can re-sort without a model re-read.</summary>
        public IReadOnlyList<DaliCircuitSeed> Seeds { get; }

        /// <summary>Geometry-only projection of <see cref="Seeds"/> — the orderer input.</summary>
        public IReadOnlyList<ZoneCircuit> ZoneCircuits { get; }

        /// <summary>The ordered, draggable circuit rows (Auto order, or the designer's Manual order).</summary>
        public ObservableCollection<DaliCircuitRowViewModel> Circuits { get; }

        /// <summary>Expand/collapse of the circuit list — pure view state, not persisted.</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        /// <summary>True once the designer has hand-ordered (dragged) this zone — frozen until Reset.</summary>
        public bool IsManual
        {
            get => _isManual;
            private set
            {
                if (SetProperty(ref _isManual, value))
                    OnPropertyChanged(nameof(SortStateText));
            }
        }

        public string SortStateText => _isManual ? "Manual ✎" : "Auto";

        public bool HasCircuits => Circuits.Count > 0;

        public string CircuitsHeader => $"Order ({Circuits.Count})";

        /// <summary>The current circuit order (top-to-bottom) — read by the snapshot builder for a Manual zone.</summary>
        public IReadOnlyList<string> CurrentOrder => Circuits.Select(c => c.CircuitKey).ToList();

        /// <summary>↺ return this zone to Auto (shown only when Manual).</summary>
        public ICommand ResetSortCommand { get; }

        /// <summary>Raised when the zone's sort changes (drag or reset) so the owning tab persists it.</summary>
        public Action? Changed { get; set; }

        /// <summary>(Re)build the ordered rows from the given room rank + sort state. Stores the rank so Reset
        /// and a later room-order change can re-sort live. The rebuild is guarded so its collection churn does
        /// not trip the Move→Manual flip.</summary>
        public void ApplyOrder(IReadOnlyDictionary<string, int> roomRank, DaliZoneSort sort)
        {
            _roomRank = roomRank ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var seedByKey = Seeds.ToDictionary(s => s.CircuitKey, StringComparer.Ordinal);
            var ordered = DaliZoneOrderer.Order(ZoneCircuits, _roomRank, sort);

            _applying = true;
            try
            {
                Circuits.Clear();
                foreach (string key in ordered)
                    if (seedByKey.TryGetValue(key, out var seed))
                        Circuits.Add(new DaliCircuitRowViewModel(seed.CircuitKey, seed.Label));
            }
            finally
            {
                _applying = false;
            }

            IsManual = sort?.Manual ?? false;
            OnPropertyChanged(nameof(HasCircuits));
            OnPropertyChanged(nameof(CircuitsHeader));
        }

        private void ResetSort()
        {
            if (!_isManual) return;
            ApplyOrder(_roomRank, DaliZoneSort.Auto);   // re-sorts live from the cached room rank
            Changed?.Invoke();
        }

        private void OnCircuitsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(HasCircuits));
            OnPropertyChanged(nameof(CircuitsHeader));

            // A drag-to-reorder is a Move: freeze the zone to Manual (its order is now the designer's) and
            // persist. Guarded so ApplyOrder's Clear/Add rebuild doesn't masquerade as a manual drag.
            if (!_applying && e.Action == NotifyCollectionChangedAction.Move)
            {
                IsManual = true;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>A location choice in a loop's assignment dropdown. Value 0 is the "&lt;Unassigned&gt;" sentinel;
    /// any positive value is a panel location number discovered in the job.</summary>
    public sealed class DaliLocationOption
    {
        public DaliLocationOption(int value, string label)
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
    /// one bus and is flagged (a warning, never an auto-split). Unassigned (<see cref="AssignedLocation"/> 0)
    /// with loads means the module is ordered job-wide but has no panel to sit in — also flagged.
    /// </summary>
    public sealed class DaliLoopRowViewModel : ViewModelBase
    {
        /// <summary>DALI-addressable loads a single bus (one module) carries. Mirrors DaliSolver.MaxLoadsPerBus;
        /// duplicated here to keep this VM free of a Services dependency (it is the display-side of the rule).</summary>
        public const int MaxLoadsPerBus = 64;

        private string _name;
        private int _assignedLocation;
        private bool _isZonesExpanded = true;

        public DaliLoopRowViewModel(string loopId, string name, int assignedLocation,
                                    IReadOnlyList<DaliLocationOption> locationOptions)
        {
            LoopId = loopId;
            _name = name;
            _assignedLocation = assignedLocation;
            LocationOptions = locationOptions;
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

        /// <summary>The location options for the assignment dropdown (0 = unassigned, then each discovered location).</summary>
        public IReadOnlyList<DaliLocationOption> LocationOptions { get; }

        /// <summary>The location this loop's module is placed in; 0 = unassigned (ordered, not placed).</summary>
        public int AssignedLocation
        {
            get => _assignedLocation;
            set
            {
                if (SetProperty(ref _assignedLocation, value))
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

        /// <summary>Has loads but no location — ordered by the job-wide demand, placed nowhere.</summary>
        public bool IsUnassigned => LoadCount > 0 && AssignedLocation <= 0;

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
                if (IsUnassigned) return $"{meter} — no location, not placed";
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
