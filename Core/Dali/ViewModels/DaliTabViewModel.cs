#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using TurboSuite.Abstractions;
using TurboSuite.Dali.Persistence;
using TurboSuite.Dali.Services;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Dali.ViewModels
{
    /// <summary>
    /// TurboDALI's loop-declaration surface: declare DALI loops (named groupings of Control Zone values) and
    /// give each the required <b>ZONE N</b> its <c>LQSE2-1DALUNV-D</c> module is placed in. This VM does the
    /// declare/group/assign/warn/persist half; the addressing + numbering lock live on the
    /// <c>DaliMainViewModel</c> that wraps it.
    ///
    /// <b>This produces placement, not order.</b> The module count and QS-link budget are the job-wide
    /// <c>DaliDemandProvider</c>/<c>DaliSolver</c> authority. TurboZones consumes the loop→zone assignments the
    /// same way — its shim builds the <c>zone → modules</c> placement map from the persisted state at its own
    /// window open (<c>DaliPlacementMapper</c>) and feeds it to the Panel Breakdown. Edits persist and reflect
    /// on the next TurboZones open (the two windows are not open in tandem in practice).
    ///
    /// Zone membership is single: a Control Zone lives in the pool or in exactly one loop. The pool + loops
    /// together always partition every DALI-carrying Control Zone in the job.
    /// </summary>
    public class DaliTabViewModel : ViewModelBase
    {
        private readonly IRevitWorkQueue _workQueue;
        private readonly IDaliLoopStore _store;
        private IReadOnlyList<DaliZoneOption> _zoneOptions;

        // Save coalescing — identical shape to the Panel Breakdown tab, so a burst of edits never drops the
        // latest snapshot to an in-flight write.
        private bool _savePending;
        private bool _saveDirty;
        private bool _loaded;   // suppress saves while the constructor builds the initial state

        /// <param name="availableZones">Control Zone values carrying DALI fixtures, with load counts.</param>
        /// <param name="availablePanelZones">The ZONE N numbers discovered in the job, for the assign dropdown.</param>
        /// <param name="saved">The persisted DALI loops (may be empty / from a v1 payload).</param>
        public DaliTabViewModel(
            IReadOnlyList<DaliZoneItemViewModel> availableZones,
            IReadOnlyList<int> availablePanelZones,
            DaliModuleState saved,
            IRevitWorkQueue workQueue,
            IDaliLoopStore store)
        {
            _workQueue = workQueue;
            _store = store;

            _zoneOptions = BuildZoneOptions(availablePanelZones, saved);
            Pool = new ObservableCollection<DaliZoneItemViewModel>();
            Loops = new ObservableCollection<DaliLoopRowViewModel>();

            // One context-sensitive create button (DMX's + New loop pattern): seeds from the pool selection
            // when any zone is picked, else an empty loop. NewLoopFromSelectionCommand remains as an explicit
            // from-selection entry point (exercised by the oracle tests).
            NewLoopCommand = new RelayCommand(() => AddLoop(fromSelection: Pool.Any(z => z.IsSelected)));
            NewLoopFromSelectionCommand = new RelayCommand(
                () => AddLoop(fromSelection: true), () => Pool.Any(z => z.IsSelected));

            LoadState(availableZones, saved);
            Recompute();
            _loaded = true;
        }

        /// <summary>Re-collect the tab from a fresh model read (the Refresh gesture) — rebuild the zone pool +
        /// load counts + panel-ZONE options and reload the persisted loops, in place so the window's bindings
        /// stay attached. Loops survive because every edit auto-saves, so the reloaded <paramref name="saved"/>
        /// already carries the designer's declarations; a zone since removed drops out and a new one appears
        /// in the pool. Saves are suppressed during the rebuild — Refresh reads, it never writes.</summary>
        public void Reseed(
            IReadOnlyList<DaliZoneItemViewModel> availableZones,
            IReadOnlyList<int> availablePanelZones,
            DaliModuleState saved)
        {
            _loaded = false;
            Pool.Clear();
            Loops.Clear();
            _zoneOptions = BuildZoneOptions(availablePanelZones, saved);
            LoadState(availableZones, saved);
            Recompute();
            _loaded = true;
        }

        public string TabHeader => "DALI";

        /// <summary>Control Zones not yet grouped into a loop. Multi-select source for the add gesture.</summary>
        public ObservableCollection<DaliZoneItemViewModel> Pool { get; }

        public ObservableCollection<DaliLoopRowViewModel> Loops { get; }

        public ICommand NewLoopCommand { get; }
        public ICommand NewLoopFromSelectionCommand { get; }

        /// <summary>True when the job has no DALI fixtures at all — the tab shows a placeholder instead.</summary>
        public bool HasDaliHardware => Pool.Count > 0 || Loops.Any(l => l.Zones.Count > 0);

        /// <summary>Loops declared — also the module count (one LQSE2-1DALUNV-D per loop).</summary>
        public int LoopCount => Loops.Count;

        /// <summary>Total DALI-addressable loads across all declared loops.</summary>
        public int TotalLoads => Loops.Sum(l => l.LoadCount);

        /// <summary>The window-header roll-up, e.g. "3 loops · 41 loads · 3 modules".</summary>
        public string SummaryText =>
            $"{LoopCount} loop{(LoopCount == 1 ? "" : "s")} · "
            + $"{TotalLoads} load{(TotalLoads == 1 ? "" : "s")} · "
            + $"{LoopCount} module{(LoopCount == 1 ? "" : "s")}";

        private int _unassignedLoopCount;
        public int UnassignedLoopCount
        {
            get => _unassignedLoopCount;
            private set
            {
                if (SetProperty(ref _unassignedLoopCount, value))
                    OnPropertyChanged(nameof(HasUnassignedLoops));
            }
        }

        public bool HasUnassignedLoops => _unassignedLoopCount > 0;

        public string UnassignedLoopMessage =>
            $"{_unassignedLoopCount} DALI loop" + (_unassignedLoopCount == 1 ? " is" : "s are")
            + " unassigned — ordered on the BOM, but not placed in any panel. Assign a ZONE to each.";

        private string _overCapMessage = "";
        public string OverCapMessage
        {
            get => _overCapMessage;
            private set
            {
                if (SetProperty(ref _overCapMessage, value))
                    OnPropertyChanged(nameof(HasOverCapLoops));
            }
        }

        public bool HasOverCapLoops => !string.IsNullOrEmpty(_overCapMessage);

        // ── Loop lifecycle ────────────────────────────────────────────────────────────────────────────

        private void AddLoop(bool fromSelection)
        {
            var row = NewRow(Guid.NewGuid().ToString("N"), NextLoopName(), assignedZone: 0);
            Loops.Add(row);

            if (fromSelection)
                MoveSelectedInto(row);

            Recompute();
            SaveSettings();
        }

        private DaliLoopRowViewModel NewRow(string loopId, string name, int assignedZone,
                                            IEnumerable<DaliZoneItemViewModel>? members = null)
        {
            var row = new DaliLoopRowViewModel(loopId, name, assignedZone, _zoneOptions);
            row.Changed = () => { if (_loaded) { Recompute(); SaveSettings(); } };
            row.AddSelectedCommand = new RelayCommand(() => MoveSelectedInto(row),
                                                      () => Pool.Any(z => z.IsSelected));
            row.RemoveCommand = new RelayCommand(() => RemoveLoop(row));
            row.RemoveZoneCommand = new RelayCommand<DaliZoneItemViewModel>(z => RemoveZoneFromLoop(row, z));

            if (members != null)
                foreach (var z in members)
                    row.Zones.Add(z);

            return row;
        }

        private void RemoveLoop(DaliLoopRowViewModel row)
        {
            // Its zones return to the pool — nothing is orphaned.
            foreach (var z in row.Zones.ToList())
            {
                z.IsSelected = false;
                Pool.Add(z);
            }
            row.Zones.Clear();
            Loops.Remove(row);
            SortPool();
            Recompute();
            SaveSettings();
        }

        private void MoveSelectedInto(DaliLoopRowViewModel row)
        {
            var selected = Pool.Where(z => z.IsSelected).ToList();
            foreach (var z in selected)
            {
                Pool.Remove(z);
                z.IsSelected = false;
                row.Zones.Add(z);
            }
            if (selected.Count > 0)
            {
                Recompute();
                SaveSettings();
            }
        }

        private void RemoveZoneFromLoop(DaliLoopRowViewModel row, DaliZoneItemViewModel zone)
        {
            if (zone == null || !row.Zones.Remove(zone)) return;
            zone.IsSelected = false;
            Pool.Add(zone);
            SortPool();
            Recompute();
            SaveSettings();
        }

        // ── State build / snapshot ────────────────────────────────────────────────────────────────────

        private void LoadState(IReadOnlyList<DaliZoneItemViewModel> availableZones, DaliModuleState saved)
        {
            // Index the live Control Zones so persisted loops can only reclaim zones that still exist.
            var byName = new Dictionary<string, DaliZoneItemViewModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var z in availableZones ?? Array.Empty<DaliZoneItemViewModel>())
                if (!byName.ContainsKey(z.ZoneName))
                    byName[z.ZoneName] = z;

            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dto in (saved?.Loops ?? new List<DaliLoopDto>()).OrderBy(l => l.Order))
            {
                var members = new List<DaliZoneItemViewModel>();
                foreach (var zoneName in dto.ZoneValues ?? new List<string>())
                {
                    if (!byName.TryGetValue(zoneName, out var item)) continue;   // renamed/deleted zone
                    if (!claimed.Add(zoneName)) continue;                        // single membership
                    members.Add(item);
                }

                // Preserve even a loop left with no live zones — the designer declared it; an empty row is
                // fixable, a silently dropped one is confusing. It orders no module until it has loads.
                // Restore the persisted LoopId — it is the durable L# anchor the numbering lock pins to, so it
                // MUST survive a reload; only a pre-LoopId (blank) payload gets a fresh one.
                Loops.Add(NewRow(string.IsNullOrWhiteSpace(dto.LoopId) ? Guid.NewGuid().ToString("N") : dto.LoopId,
                                 string.IsNullOrWhiteSpace(dto.Name) ? NextLoopName() : dto.Name,
                                 dto.AssignedZone, members));
            }

            // Everything not claimed by a loop starts in the pool.
            foreach (var z in availableZones ?? Array.Empty<DaliZoneItemViewModel>())
                if (!claimed.Contains(z.ZoneName))
                    Pool.Add(z);

            SortPool();
        }

        private void SortPool()
        {
            var sorted = Pool.OrderBy(z => z.ZoneName, NaturalStringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                int current = Pool.IndexOf(sorted[i]);
                if (current != i) Pool.Move(current, i);
            }
        }

        private void Recompute()
        {
            UnassignedLoopCount = Loops.Count(l => l.IsUnassigned);

            var overCap = Loops.Where(l => l.IsOverCap)
                               .Select(l => $"\"{l.Name}\" ({l.LoadCount})")
                               .ToList();
            OverCapMessage = overCap.Count == 0
                ? ""
                : $"{overCap.Count} DALI loop" + (overCap.Count == 1 ? "" : "s")
                  + $" exceed {DaliLoopRowViewModel.MaxLoadsPerBus} loads on one bus — "
                  + string.Join("; ", overCap) + " — split into more loops.";

            OnPropertyChanged(nameof(HasDaliHardware));
            OnPropertyChanged(nameof(LoopCount));
            OnPropertyChanged(nameof(TotalLoads));
            OnPropertyChanged(nameof(SummaryText));
        }

        private DaliModuleState BuildSnapshot()
        {
            var state = new DaliModuleState { PayloadVersion = 2 };
            int order = 0;
            foreach (var row in Loops)
            {
                state.Loops.Add(new DaliLoopDto
                {
                    LoopId = row.LoopId,
                    Name = row.Name ?? "",
                    Order = order++,
                    AssignedZone = row.AssignedZone,
                    ZoneValues = row.Zones.Select(z => z.ZoneName).ToList()
                });
            }
            return state;
        }

        // ── Persistence (coalesced, mirrors PanelBreakdownTabViewModel) ─────────────────────────────────

        private void SaveSettings()
        {
            if (!_loaded) return;
            if (_savePending) { _saveDirty = true; return; }
            RaiseSave();
        }

        private void RaiseSave()
        {
            _savePending = true;
            _saveDirty = false;

            var snapshot = BuildSnapshot();
            _workQueue.Enqueue(
                () => { _store.Save(snapshot); return null!; },
                _ =>
                {
                    if (_saveDirty) RaiseSave();
                    else _savePending = false;
                });
        }

        private static IReadOnlyList<DaliZoneOption> BuildZoneOptions(
            IReadOnlyList<int> panelZones, DaliModuleState saved)
        {
            // Discovered zones ∪ any zone a persisted loop is already assigned to — so a loop assigned to a
            // zone that has since lost its dimming circuits still shows its assignment rather than blank.
            var zones = new SortedSet<int>((panelZones ?? Array.Empty<int>()).Where(z => z > 0));
            foreach (var loop in saved?.Loops ?? new List<DaliLoopDto>())
                if (loop.AssignedZone > 0) zones.Add(loop.AssignedZone);

            var options = new List<DaliZoneOption> { new DaliZoneOption(0, "<Unassigned>") };
            foreach (int z in zones)
                options.Add(new DaliZoneOption(z, $"ZONE {z}"));
            return options;
        }

        private string NextLoopName()
        {
            // First "Loop N" not already taken, so re-adding after a delete doesn't collide.
            var taken = new HashSet<string>(Loops.Select(l => l.Name ?? ""), StringComparer.OrdinalIgnoreCase);
            for (int n = 1; ; n++)
            {
                string candidate = $"Loop {n}";
                if (!taken.Contains(candidate)) return candidate;
            }
        }
    }
}
