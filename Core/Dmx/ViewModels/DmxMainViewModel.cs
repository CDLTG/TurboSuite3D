#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.ComponentModel;
using TurboSuite.Abstractions;
using TurboSuite.Dmx.Input;
using TurboSuite.Dmx.Lock;
using TurboSuite.Dmx.OneLine;
using TurboSuite.Dmx.Overlay;
using TurboSuite.Dmx.Persistence;
using TurboSuite.Dmx.Placement;
using TurboSuite.Dmx.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Dmx.ViewModels
{
    /// <summary>
    /// The TurboDMX window's ViewModel — the loop-centric build surface. The designer sets the declarations
    /// (profile, Kind-2 settings, curated decoder/driver pools), then works the model loop by loop: zones
    /// start in the <see cref="ZonePool"/> (the engine auto-packs them), and the designer pulls them into
    /// declared <see cref="Loops"/> — each a tree node owning its assigned zones (and each zone its cluster
    /// sub-builder, §8d). The right-hand bill is the always-on whole-system roll-up (interfaces / links /
    /// processors / breakers — only complete once every loop is declared). Placement is the loop: each loop
    /// carries its own Place action + placement state. The solve stays whole-system under the hood (DEC #s
    /// are system-wide 1..N), so per-loop placement just stamps the numbers the solve already assigned.
    /// </summary>
    public sealed class DmxMainViewModel : ViewModelBase
    {
        private readonly IRevitWorkQueue? _workQueue;
        private readonly IDmxModelReader? _reader;
        private readonly IDmxPlacementService? _placement;
        private readonly IDmxOneLineService? _oneLine;
        private readonly IDmxZoneColorService? _zoneColor;
        private readonly IDmxModelSelection? _selection;
        private readonly Action<DmxModuleState>? _persist;
        private readonly Func<string, bool>? _confirm;   // shim Yes/No gate for the destructive lock actions
        private readonly DmxJobSettings _settings = new DmxJobSettings();

        // Fixture ElementId → its Control Zone, so a model selection can be filtered to one zone's runs (§8d).
        private Dictionary<long, string> _zoneByFixtureId = new Dictionary<long, string>();
        // Zone value → run (fixture) count — drives the pool's "(N)" and a zone's cluster splittability.
        private Dictionary<string, int> _runsByZone = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _clusterSeq;

        // The last successful solve + its lock-aware numbering — kept so Place stamps the same DEC #s the bill
        // shows. Null whenever the current declarations don't solve (empty/guidance/gate error).
        private DmxBill? _lastBill;
        private DmxNumbering? _lastNumbering;
        private bool _placing;   // guard against re-entrant Place while a pick is open

        // The last-loaded module state — preserved so a save round-trips the overlays this VM does NOT yet
        // manage (control-system tags, the solve snapshot); BuildState() overwrites Settings + Loops and
        // carries the rest through untouched. Clusters + the placement registry are managed here.
        private DmxModuleState _loadedState = new DmxModuleState();

        private IReadOnlyList<DmxFixtureReading> _fixtures;
        private int _loopSeq;
        private bool _loaded;   // gate persistence until the constructor's initial load + Run complete

        // Saved curation (UniqueId sets) + saved loops, consumed once by the first LoadSnapshot so the
        // window reopens with the designer's ticks and declared loops instead of the all-selected default.
        private HashSet<string>? _savedDecoderTypeIds;
        private HashSet<string>? _savedDriverTypeIds;
        private List<DmxLoopDto>? _initialLoops;

        public DmxMainViewModel(DmxModelSnapshot snapshot,
                                DmxModuleState? state = null,
                                IRevitWorkQueue? workQueue = null,
                                IDmxModelReader? reader = null,
                                Action<DmxModuleState>? persist = null,
                                IDmxPlacementService? placement = null,
                                Func<string, bool>? confirm = null,
                                IDmxModelSelection? selection = null,
                                IDmxOneLineService? oneLine = null,
                                IDmxZoneColorService? zoneColor = null)
        {
            _workQueue = workQueue;
            _reader = reader;
            _placement = placement;
            _oneLine = oneLine;
            _zoneColor = zoneColor;
            _selection = selection;
            _persist = persist;
            _confirm = confirm;

            Profiles = new ObservableCollection<DmxProfile>(DmxProfile.All);
            _selectedProfile = DmxProfile.Lutron;

            DecoderRows = new ObservableCollection<DmxDecoderRowViewModel>();
            DriverRows = new ObservableCollection<DmxDriverRowViewModel>();
            Loops = new ObservableCollection<DmxLoopRowViewModel>();
            ZonePool = new ObservableCollection<DmxZonePoolItemViewModel>();
            WireLegend = new ObservableCollection<DmxWireLegendEntry>();
            ZoneNames = new List<string>();
            _bill = DmxBillViewModel.Empty("Run to compute the bill.");
            _fixtures = new List<DmxFixtureReading>();

            RunCommand = new RelayCommand(Run);
            NewEmptyLoopCommand = new RelayCommand(NewEmptyLoop);
            NewLoopFromSelectionCommand = new RelayCommand(NewLoopFromSelection);
            RefreshCommand = new RelayCommand(Refresh, () => _workQueue != null && _reader != null);
            // One button does both: Lock (first time) and Re-lock (re-baseline when already Locked).
            LockCommand = new RelayCommand(Lock, () => _lastNumbering != null);
            UnlockCommand = new RelayCommand(Unlock, () => IsLocked);
            DrawWireLegendCommand = new RelayCommand(DrawWireLegend, CanDrawWireLegend);

            ApplyPersistedState(state);
            LoadSnapshot(snapshot);
            Run();
            _loaded = true;   // any later mutation now persists
            ApplyZoneColors();   // Phase 5: color the active view by Control Zone while the window is open
        }

        // ── Declarations: profile ───────────────────────────────────────────────────────────────────
        public ObservableCollection<DmxProfile> Profiles { get; }

        private DmxProfile _selectedProfile;
        public DmxProfile SelectedProfile
        {
            get => _selectedProfile;
            set { if (SetProperty(ref _selectedProfile, value ?? DmxProfile.Lutron)) { Run(); Persist(); } }
        }

        // ── Declarations: Kind-2 job settings (profile-seeded, overridable) ─────────────────────────
        public double SystemVolts { get => _settings.SystemVolts; set { _settings.SystemVolts = value; OnPropertyChanged(); Persist(); } }
        public double BreakerAmps { get => _settings.BreakerAmps; set { _settings.BreakerAmps = value; OnPropertyChanged(); Persist(); } }
        public double FeedVolts { get => _settings.FeedVolts; set { _settings.FeedVolts = value; OnPropertyChanged(); Persist(); } }
        public double BreakerContinuousDerate { get => _settings.BreakerContinuousDerate; set { _settings.BreakerContinuousDerate = value; OnPropertyChanged(); Persist(); } }
        public int MaxDriversPerBreaker { get => _settings.MaxDriversPerBreaker; set { _settings.MaxDriversPerBreaker = value; OnPropertyChanged(); Persist(); } }
        public int MaxDevicesPerSegment { get => _settings.MaxDevicesPerSegment; set { _settings.MaxDevicesPerSegment = value; OnPropertyChanged(); Persist(); } }
        public int ReservedChannels { get => _settings.ReservedChannels; set { _settings.ReservedChannels = value; OnPropertyChanged(); Persist(); } }

        /// <summary>Job-wide homerun pull-up (Phase 6). Bumping it re-derives the wire legend, so refresh it.</summary>
        public int PullUpSizes
        {
            get => _settings.PullUpSizes;
            set { if (_settings.PullUpSizes == value) return; _settings.PullUpSizes = value < 0 ? 0 : value; OnPropertyChanged(); RefreshWireLegend(); Persist(); }
        }

        // ── Declarations: curated part pools ────────────────────────────────────────────────────────
        public ObservableCollection<DmxDecoderRowViewModel> DecoderRows { get; }
        public ObservableCollection<DmxDriverRowViewModel> DriverRows { get; }

        // ── The loop-centric work surface: a pool of unassigned zones + declared loops ───────────────
        /// <summary>Zones not yet pulled into a loop — the engine auto-packs these (the "(unassigned)"
        /// residual). The multi-select source for the assignment gesture.</summary>
        public ObservableCollection<DmxZonePoolItemViewModel> ZonePool { get; }

        /// <summary>The declared loops (tree roots) — each owns its assigned zones (and each zone its cluster
        /// sub-builder), plus its own Place action + placement state.</summary>
        public ObservableCollection<DmxLoopRowViewModel> Loops { get; }

        private string _builderStatus = "";
        /// <summary>Status line for the loop/cluster builder (assignment + clustering feedback).</summary>
        public string BuilderStatus { get => _builderStatus; private set => SetProperty(ref _builderStatus, value); }

        /// <summary>Whether the cluster sub-builder can act (needs the Revit selection seam + work queue).</summary>
        public bool CanCluster => _selection != null && _workQueue != null;

        public IReadOnlyList<string> ZoneNames { get; private set; }

        // ── Model summary ───────────────────────────────────────────────────────────────────────────
        public int FixtureCount => _fixtures.Count;
        public int ZoneCount => ZoneNames.Count;

        private int _unassignedFixtures;
        public int UnassignedFixtures { get => _unassignedFixtures; private set => SetProperty(ref _unassignedFixtures, value); }

        public string SummaryText =>
            $"{FixtureCount} DMX fixtures · {ZoneCount} zones" +
            (UnassignedFixtures > 0 ? $" · {UnassignedFixtures} unassigned" : "");

        // ── The bill (right zone) ────────────────────────────────────────────────────────────────────
        private DmxBillViewModel _bill;
        public DmxBillViewModel Bill { get => _bill; private set => SetProperty(ref _bill, value); }

        // ── Generated wire legend (Phase 6) — dense, per-job, rebuilt off the last solve + pull-up ─────
        /// <summary>The per-job wire legend rows (number ↔ type), regenerated on each solve and when the
        /// pull-up changes. The same numbers the planner stamps on the one-line's <c>WireMark</c> markers.</summary>
        public ObservableCollection<DmxWireLegendEntry> WireLegend { get; }

        private void RefreshWireLegend()
        {
            WireLegend.Clear();
            if (_lastBill == null) return;
            foreach (var entry in DmxWireLegend.ForBill(_lastBill, _settings.PullUpSizes).Entries)
                WireLegend.Add(entry);
        }

        // ── Placement status (footer) ────────────────────────────────────────────────────────────────
        private string _placementStatus = "";
        public string PlacementStatus { get => _placementStatus; private set => SetProperty(ref _placementStatus, value); }

        // ── Numbering lock (§8c) — state lives in the persisted snapshot ─────────────────────────────
        public bool IsLocked =>
            string.Equals(_loadedState.Snapshot?.NumberingState, "Locked", StringComparison.OrdinalIgnoreCase);

        public string LockStateText => IsLocked
            ? "LOCKED — issued DEC #s preserved; conflicts flag REVIEW"
            : "UNLOCKED — numbering re-derives freely each run";

        /// <summary>The Lock button's label — "Lock" first time, "Re-lock" once Locked (re-baseline).</summary>
        public string LockButtonText => IsLocked ? "Re-lock" : "Lock";

        // ── Commands ─────────────────────────────────────────────────────────────────────────────────
        public ICommand RunCommand { get; }
        public ICommand NewEmptyLoopCommand { get; }
        public ICommand NewLoopFromSelectionCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand LockCommand { get; }
        public ICommand UnlockCommand { get; }
        public ICommand DrawWireLegendCommand { get; }

        /// <summary>The pure solve (TurboDMX-Design §1.5 pipeline). Idempotent — safe to call constantly.
        /// On every exit it refreshes each loop's placement state + the Place buttons' enabled state.</summary>
        public void Run()
        {
            _lastBill = null; _lastNumbering = null;   // invalidate the placement plan until a clean solve below
            try
            {
                var zoneResult = DmxZoneBuilder.Build(_fixtures, _loadedState.Clusters);
                if (zoneResult.Zones.Count == 0)
                {
                    Bill = DmxBillViewModel.Empty("No DMX fixtures with a Control Zone assigned.");
                    return;
                }

                var decoders = DecoderRows.Where(r => r.IsSelected).Select(r => r.Candidate).ToList();
                if (decoders.Count == 0) { Bill = DmxBillViewModel.Empty("Tick at least one decoder type."); return; }

                var drivers = DriverRows.Where(r => r.IsSelected).Select(r => r.Candidate).ToList();
                if (drivers.Count == 0) { Bill = DmxBillViewModel.Empty("Tick at least one driver type."); return; }

                var contract = DmxContractBuilder.Build(SelectedProfile, _settings, decoders, drivers);
                var loops = Loops.Select(l => l.ToDeclaration()).Where(d => d != null).Cast<LoopDeclaration>().ToList();

                try
                {
                    var bill = DmxSolver.Solve(contract, zoneResult.Zones, loops);
                    _lastBill = bill;

                    // Lock-aware numbering (§8c): Unlocked ⇒ fresh 1..N; Locked ⇒ pin to the snapshot baseline,
                    // append additive decoders, flag type/interface drift as REVIEW.
                    var solved = DmxBillFlattener.Flatten(bill);
                    _lastNumbering = DmxLockReconciler.Reconcile(solved, _loadedState.Snapshot, IsLocked);
                    Bill = DmxBillViewModel.FromBill(bill, SelectedProfile.ChannelCeiling,
                                                     _lastNumbering.Reviews.Select(r => r.Message));
                }
                catch (UnmappableTapeException ex) { Bill = DmxBillViewModel.Error(ex.Message); }
                catch (OverCapRunsException ex) { Bill = DmxBillViewModel.Error(ex.Message); }
                catch (OverCapLoopsException ex) { Bill = DmxBillViewModel.Error(ex.Message); }
                catch (LoopDeclarationException ex) { Bill = DmxBillViewModel.Error(ex.Message); }
                // Catch-all so a bad declaration can NEVER escape onto Revit's UI thread (an unhandled
                // exception there is a fatal Revit crash, not a dialog). Covers the engine's other refusals —
                // a mixed-channel zone (ArgumentException), a breaker cap too small for a driver
                // (InvalidOperationException), a non-positive cap (ArgumentOutOfRangeException) — and anything
                // unforeseen. The bill shows the message as a red verdict; the window stays alive.
                catch (Exception ex) { Bill = DmxBillViewModel.Error(ex.Message); }
            }
            finally
            {
                RefreshWireLegend();
                UpdateLoopInterfaceNumbers();
                RaisePlaceCanExecute();
            }
        }

        // ── Per-loop placement (BuildPlan Phase 2/3: the loop is the placement unit) ──────────────────

        /// <summary>Refresh each loop's interface # from the last solve (0 when there's no clean solve), so the
        /// per-loop Place / one-line / legend actions target the right interface. Called after every Run and
        /// every Place. (Placement is idempotent via orphan cleanup, so no placed/unplaced state is tracked.)</summary>
        private void UpdateLoopInterfaceNumbers()
        {
            var ifaceByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (_lastBill != null)
                foreach (var i in _lastBill.Interfaces)
                    if (i.Interface.LoopName != null) ifaceByName[i.Interface.LoopName] = i.Interface.InterfaceNumber;

            foreach (var loop in Loops)
                loop.InterfaceNumber = _lastNumbering != null && _lastBill != null
                    && ifaceByName.TryGetValue(loop.Name, out var n) ? n : 0;
        }

        private bool CanPlaceLoop(DmxLoopRowViewModel loop) =>
            _placement != null && _workQueue != null && !_placing
            && _lastBill != null && _lastNumbering != null && loop.InterfaceNumber > 0;

        /// <summary>Place just this loop: build the full system plan off the last solve (so DEC #s + orphan
        /// validity stay whole-system), then hand the shim only this loop's interface # to pick + place. One
        /// pick lands this loop's decoders + drivers; orphan cleanup still reconciles against the full solve.</summary>
        private void PlaceLoop(DmxLoopRowViewModel loop)
        {
            if (!CanPlaceLoop(loop)) return;

            var decoderMap = DecoderRows.Where(r => r.IsSelected)
                .GroupBy(r => r.Candidate.Name).ToDictionary(g => g.Key, g => g.First().Candidate.TypeId);
            var driverMap = DriverRows.Where(r => r.IsSelected)
                .GroupBy(r => r.Candidate.Name).ToDictionary(g => g.Key, g => g.First().Candidate.TypeId);

            var plan = DmxPlacementPlanner.Build(_lastBill!, _lastNumbering!, decoderMap, driverMap);
            var registry = _loadedState.Placed
                .Select(p => new DmxPlacedPair(p.Dec, p.DecoderId, p.DriverId)).ToList();
            bool locked = IsLocked;
            int iface = loop.InterfaceNumber;
            string label = loop.Name;

            _placing = true;
            RaisePlaceCanExecute();
            PlacementStatus = $"Placing loop \"{label}\"… pick a point in the model (Esc to cancel).";

            _workQueue!.Enqueue(
                () => _placement!.Place(plan, locked, registry, iface),
                result =>
                {
                    _placing = false;
                    if (result is DmxPlacementResult r)
                    {
                        MergeRegistry(r);
                        PlacementStatus = r.Summary;
                        UpdateLoopInterfaceNumbers();
                    }
                    else PlacementStatus = "Placement failed.";
                    RaisePlaceCanExecute();
                });
        }

        // RelayCommand re-queries via CommandManager.RequerySuggested; nudge it so the Place/Lock buttons'
        // enabled state tracks _placing / _lastBill / lock changes that don't ride a normal UI input event.
        private static void RaisePlaceCanExecute() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

        /// <summary>Fold a placement result into the persisted registry: drop removed orphans, add the newly
        /// placed pairs (last-wins by DEC #), then persist so a later re-Place cleans up exactly.</summary>
        private void MergeRegistry(DmxPlacementResult r)
        {
            if (r.RemovedDecs.Count == 0 && r.PlacedPairs.Count == 0) return;

            var removed = new HashSet<int>(r.RemovedDecs);
            var added = new HashSet<int>(r.PlacedPairs.Select(p => p.Dec));
            var kept = _loadedState.Placed
                .Where(p => !removed.Contains(p.Dec) && !added.Contains(p.Dec))
                .ToList();
            kept.AddRange(r.PlacedPairs.Select(p => new DmxPlacedPairDto
            {
                Dec = p.Dec, DecoderId = p.DecoderId, DriverId = p.DriverId,
            }));
            _loadedState.Placed = kept;
            Persist();
        }

        // ── Per-loop one-line (Phase 4): draw/redraw this loop's owned Drafting View ──────────────────
        private bool _drawing;
        private bool CanDrawOneLine(DmxLoopRowViewModel loop) =>
            _oneLine != null && _workQueue != null && !_drawing
            && _lastBill != null && _lastNumbering != null && loop.InterfaceNumber > 0;

        /// <summary>Draw just this loop's one-line: plan ALL loops' drawings off the last solve (so DEC #s match
        /// placement), then hand the shim this loop's interface # to wipe-and-redraw its owned view. The
        /// returned view id is folded into the persisted registry so the next draw reuses the same view.</summary>
        private void DrawOneLine(DmxLoopRowViewModel loop)
        {
            if (!CanDrawOneLine(loop)) return;

            var driverMarks = DriverRows
                .GroupBy(r => r.Candidate.Name)
                .ToDictionary(g => g.Key, g => g.First().Candidate.TypeMark);
            var drawings = DmxOneLinePlanner.Build(_lastBill!, _lastNumbering!, driverMarks, _settings.PullUpSizes);
            var registry = _loadedState.OneLineViews.ToDictionary(v => v.InterfaceNumber, v => v.ViewId);
            int iface = loop.InterfaceNumber;
            string label = loop.Name;

            _drawing = true;
            RaisePlaceCanExecute();
            PlacementStatus = $"Drawing one-line for loop \"{label}\"…";

            _workQueue!.Enqueue(
                () => _oneLine!.Draw(drawings, SystemName, iface, registry),
                result =>
                {
                    _drawing = false;
                    if (result is DmxOneLineResult r)
                    {
                        if (r.Ok) RegisterOneLineView(iface, r.ViewId);
                        PlacementStatus = r.Summary;
                    }
                    else PlacementStatus = "One-line draw failed.";
                    RaisePlaceCanExecute();
                });
        }

        // ── Per-job wire legend view (BuildPlan Phase 6) ─────────────────────────────────────────────
        private bool CanDrawWireLegend() =>
            _oneLine != null && _workQueue != null && !_drawing && _lastBill != null && WireLegend.Count > 0;

        /// <summary>Draw the single per-job wire-legend view off the last solve's legend (the same numbers the
        /// one-line stamps on every wire), then fold the owned view id into the persisted state.</summary>
        private void DrawWireLegend()
        {
            if (!CanDrawWireLegend()) return;

            var legend = DmxWireLegend.ForBill(_lastBill!, _settings.PullUpSizes);
            var drawing = DmxWireLegendPlanner.Build(legend);
            long existingId = _loadedState.WireLegendViewId;

            _drawing = true;
            RaisePlaceCanExecute();
            PlacementStatus = "Drawing wire legend…";

            _workQueue!.Enqueue(
                () => _oneLine!.DrawWireLegend(drawing, SystemName, existingId),
                result =>
                {
                    _drawing = false;
                    if (result is DmxWireLegendResult r)
                    {
                        if (r.Ok) { _loadedState.WireLegendViewId = r.ViewId; Persist(); }
                        PlacementStatus = r.Summary;
                    }
                    else PlacementStatus = "Wire-legend draw failed.";
                    RaisePlaceCanExecute();
                });
        }

        /// <summary>The Control System label seeding the owned view names. One system per window today.</summary>
        public string SystemName { get; set; } = "DMX";

        /// <summary>Record this loop's owned one-line view id (last-wins by interface #), then persist.</summary>
        private void RegisterOneLineView(int interfaceNumber, long viewId)
        {
            var kept = _loadedState.OneLineViews.Where(v => v.InterfaceNumber != interfaceNumber).ToList();
            kept.Add(new DmxOneLineViewDto { InterfaceNumber = interfaceNumber, ViewId = viewId });
            _loadedState.OneLineViews = kept;
            Persist();
        }

        // ── Numbering lock lifecycle (§8c): Unlocked ⇄ Locked ───────────────────────────────────────
        // One Lock action does double duty: the first press snapshots the current numbering as the frozen
        // baseline; pressing it again while Locked RE-baselines (re-issue) — confirmed, and crucially never
        // renumbers (it captures the current pinned numbers, unlike Unlock→Lock which would clobber to 1..N).
        // Unlock clears the baseline back to clobber-freely. State + baseline persist in the DMX snapshot.

        private void Lock()
        {
            if (_lastNumbering == null) return;

            // Already Locked ⇒ this is a deliberate re-baseline; gate it (issued numbers are at stake).
            if (IsLocked && !(_confirm?.Invoke("Re-lock numbering to a NEW baseline?\n\n"
                + "The current DEC #s (including any additions since the last lock) become the new issued "
                + "baseline, and any REVIEW flags clear. The numbers themselves don't change. Only do this for "
                + "a sanctioned re-issue.") ?? true)) return;

            _loadedState.Snapshot = DmxSnapshotBuilder.Capture(_lastNumbering, "Locked");
            OnLockChanged();
            Run();      // re-solve now pins to the just-captured baseline (identical numbers, no REVIEW)
            Persist();
        }

        private void Unlock()
        {
            if (!IsLocked) return;
            if (!(_confirm?.Invoke("Unlock numbering?\n\n"
                + "Re-runs will renumber freely (clobber) and the issued baseline is discarded. "
                + "Use this only before the numbering lockdown point.") ?? true)) return;

            _loadedState.Snapshot = new DmxSnapshotDto { NumberingState = "Unlocked" };
            OnLockChanged();
            Run();
            Persist();
        }

        private void OnLockChanged()
        {
            OnPropertyChanged(nameof(IsLocked));
            OnPropertyChanged(nameof(LockStateText));
            OnPropertyChanged(nameof(LockButtonText));
            RaisePlaceCanExecute();
        }

        // ── Loop assignment (the pool → loop gesture; destination owns the button) ────────────────────

        private List<string> SelectedPoolZones() =>
            ZonePool.Where(z => z.IsSelected).Select(z => z.ZoneName).ToList();

        private void NewEmptyLoop()
        {
            var loop = WireLoop(new DmxLoopRowViewModel($"Loop {++_loopSeq}"));
            Loops.Add(loop);
            BuilderStatus = $"Added empty loop \"{loop.Name}\".";
            Persist();              // no zones ⇒ no solve change, but persist the new loop
            RaisePlaceCanExecute();
        }

        private void NewLoopFromSelection()
        {
            var sel = SelectedPoolZones();
            var loop = WireLoop(new DmxLoopRowViewModel($"Loop {++_loopSeq}"));
            Loops.Add(loop);
            foreach (var zn in sel) loop.Zones.Add(MakeLoopZone(loop, zn));
            BuilderStatus = sel.Count > 0
                ? $"New loop \"{loop.Name}\" with {sel.Count} zone(s)."
                : $"Added empty loop \"{loop.Name}\" — select pool zones to fill it.";
            AfterAssignmentEdit();
        }

        private void AddSelectionToLoop(DmxLoopRowViewModel loop)
        {
            var sel = SelectedPoolZones();
            if (sel.Count == 0) { BuilderStatus = "Select zone(s) in the pool first, then '+ Add'."; return; }
            int added = 0;
            foreach (var zn in sel)
            {
                if (loop.Zones.Any(z => string.Equals(z.ZoneName, zn, StringComparison.OrdinalIgnoreCase))) continue;
                loop.Zones.Add(MakeLoopZone(loop, zn));
                added++;
            }
            BuilderStatus = $"Added {added} zone(s) to \"{loop.Name}\".";
            AfterAssignmentEdit();
        }

        private void RemoveLoop(DmxLoopRowViewModel loop)
        {
            if (!Loops.Remove(loop)) return;
            BuilderStatus = $"Removed \"{loop.Name}\" — its zones returned to the pool.";
            AfterAssignmentEdit();
        }

        private void RemoveZoneFromLoop(DmxLoopRowViewModel loop, DmxLoopZoneViewModel zone)
        {
            if (!loop.Zones.Remove(zone)) return;
            BuilderStatus = $"\"{zone.ZoneName}\" returned to the pool.";
            AfterAssignmentEdit();
        }

        /// <summary>After any zone↔loop change: recompute the pool from current loop membership, re-solve
        /// (interfaces shifted), and persist.</summary>
        private void AfterAssignmentEdit()
        {
            RebuildPool();
            Run();
            Persist();
        }

        private DmxLoopRowViewModel WireLoop(DmxLoopRowViewModel loop)
        {
            loop.PropertyChanged += (_, __) => Persist();       // name edits
            loop.AddSelectedCommand = new RelayCommand(() => AddSelectionToLoop(loop));
            loop.RemoveCommand = new RelayCommand(() => RemoveLoop(loop));
            loop.PlaceCommand = new RelayCommand(() => PlaceLoop(loop), () => CanPlaceLoop(loop));
            loop.DrawOneLineCommand = new RelayCommand(() => DrawOneLine(loop), () => CanDrawOneLine(loop));
            return loop;
        }

        /// <summary>Build a zone node inside a loop: its run count (splittability) + cluster sub-builder + the
        /// "← (to pool)" action.</summary>
        private DmxLoopZoneViewModel MakeLoopZone(DmxLoopRowViewModel loop, string zoneName)
        {
            _runsByZone.TryGetValue(zoneName, out int total);
            var z = new DmxLoopZoneViewModel(zoneName, total);
            z.RemoveFromLoopCommand = new RelayCommand(() => RemoveZoneFromLoop(loop, z));
            z.NewClusterCommand = new RelayCommand(() => NewClusterFromSelection(z), () => CanCluster);
            RefreshClusterRows(z);
            return z;
        }

        private void RebuildPool()
        {
            var assigned = new HashSet<string>(
                Loops.SelectMany(l => l.Zones.Select(z => z.ZoneName)), StringComparer.OrdinalIgnoreCase);

            ZonePool.Clear();
            foreach (var zone in ZoneNames)
            {
                if (assigned.Contains(zone)) continue;
                _runsByZone.TryGetValue(zone, out int rc);
                ZonePool.Add(new DmxZonePoolItemViewModel(zone, rc));
            }
        }

        // ── Persistence (doc-side ExtensibleStorage via the injected save callback) ─────────────────
        //
        // Every persisted declaration — profile, Kind-2 settings, curated part-pool ticks, declared loops —
        // funnels through Persist() on change. The injected callback (shim-side) coalesces and writes the
        // bundle to the DMX schema on the Revit thread. No-op until the constructor finishes (_loaded) so the
        // initial load doesn't write the model back to itself, and a no-op when constructed without a persister.

        /// <summary>Restore the saved declarations onto the window before the model read. Consumed once;
        /// the saved curation/loops are stashed for the first <see cref="LoadSnapshot"/> to apply.</summary>
        private void ApplyPersistedState(DmxModuleState? state)
        {
            _loadedState = state ?? new DmxModuleState();
            var s = _loadedState.Settings ?? new DmxSettingsDto();

            _selectedProfile = DmxProfile.All.FirstOrDefault(p =>
                string.Equals(p.Name, s.Profile, StringComparison.OrdinalIgnoreCase)) ?? DmxProfile.Lutron;

            _settings.SystemVolts = s.SystemVolts;
            _settings.BreakerAmps = s.BreakerAmps;
            _settings.FeedVolts = s.FeedVolts;
            _settings.BreakerContinuousDerate = s.BreakerContinuousDerate;
            _settings.MaxDriversPerBreaker = s.MaxDriversPerBreaker;
            _settings.MaxDevicesPerSegment = s.MaxDevicesPerSegment;
            _settings.ReservedChannels = s.ReservedChannels;
            _settings.PullUpSizes = s.PullUpSizes;
            _settings.BreakerBasis = Enum.TryParse<BreakerBasis>(s.BreakerBasis, out var basis)
                ? basis : BreakerBasis.ConnectedLoad;

            // Empty saved list ⇒ never curated ⇒ leave null so LoadSnapshot defaults to all-selected.
            _savedDecoderTypeIds = s.DecoderTypeIds?.Count > 0 ? new HashSet<string>(s.DecoderTypeIds) : null;
            _savedDriverTypeIds = s.DriverTypeIds?.Count > 0 ? new HashSet<string>(s.DriverTypeIds) : null;
            _initialLoops = _loadedState.Loops?.Count > 0 ? _loadedState.Loops : null;
        }

        /// <summary>Build the current declarations into a <see cref="DmxModuleState"/>, carrying through the
        /// overlays this VM doesn't manage (control-system tags, snapshot) from the last load. Loops persist
        /// as their assigned zones (chain order); the pool is derived, not stored.</summary>
        private DmxModuleState BuildState()
        {
            var state = _loadedState;   // preserve PayloadVersion + the unmanaged overlays + clusters + registry
            state.Settings = new DmxSettingsDto
            {
                Profile = _selectedProfile.Name,
                SystemVolts = _settings.SystemVolts,
                BreakerAmps = _settings.BreakerAmps,
                FeedVolts = _settings.FeedVolts,
                BreakerContinuousDerate = _settings.BreakerContinuousDerate,
                MaxDriversPerBreaker = _settings.MaxDriversPerBreaker,
                MaxDevicesPerSegment = _settings.MaxDevicesPerSegment,
                ReservedChannels = _settings.ReservedChannels,
                PullUpSizes = _settings.PullUpSizes,
                BreakerBasis = _settings.BreakerBasis.ToString(),
                DecoderTypeIds = DecoderRows.Where(r => r.IsSelected).Select(r => r.Candidate.TypeId).ToList(),
                DriverTypeIds = DriverRows.Where(r => r.IsSelected).Select(r => r.Candidate.TypeId).ToList(),
            };
            state.Loops = Loops.Select((l, i) => new DmxLoopDto
            {
                LoopId = i.ToString(),
                Name = l.Name,
                Order = i,
                ZoneValues = l.AssignedZoneNames.ToList(),
            }).ToList();
            return state;
        }

        private void Persist()
        {
            if (!_loaded || _persist == null) return;
            _persist(BuildState());
        }

        /// <summary>Subscribe a part-pool row's tick to persistence.</summary>
        private T WireRow<T>(T row) where T : INotifyPropertyChanged
        {
            row.PropertyChanged += (_, __) => Persist();
            return row;
        }

        /// <summary>Re-read the model on the Revit thread (work queue), then rebuild + re-solve. No-op when
        /// constructed without a reader (e.g. unit tests).</summary>
        public void Refresh()
        {
            if (_workQueue == null || _reader == null) return;
            _workQueue.Enqueue(
                () => _reader.Read(),
                result => { LoadSnapshot((DmxModelSnapshot)result); Run(); ApplyZoneColors(); });
        }

        // ── Phase 5: Control-Zone color overlay (active view, live only while the window is open) ──────
        /// <summary>Color the active view's DMX fixtures by Control Zone. Best-effort: no-op when there's no
        /// overlay service, no zones, or the view is template-locked (the shim returns a notice). Re-run on
        /// open and after each Refresh so the palette tracks the current zone set.</summary>
        private void ApplyZoneColors()
        {
            if (_zoneColor == null || _workQueue == null) return;
            var palette = DmxZonePalette.Build(ZoneNames);
            if (palette.Count == 0) return;
            _workQueue.Enqueue(
                () => _zoneColor.Apply(palette),
                result => { if (result is string s && s.Length > 0) PlacementStatus = s; });
        }

        /// <summary>Revert the overlay (window close). Invokes <paramref name="onComplete"/> on the UI thread
        /// once the Revit-side removal has run, so the shim can DEFER the actual window close until the view
        /// is clean. Calls back immediately when there's nothing to revert.</summary>
        public void RevertZoneColors(Action? onComplete = null)
        {
            if (_zoneColor == null || _workQueue == null) { onComplete?.Invoke(); return; }
            _workQueue.Enqueue(() => _zoneColor.Revert(), _ => onComplete?.Invoke());
        }

        // ── Snapshot load (preserving selections + loops across a refresh) ──────────────────────────
        private void LoadSnapshot(DmxModelSnapshot snapshot)
        {
            snapshot ??= new DmxModelSnapshot();
            _fixtures = snapshot.Fixtures;
            _zoneByFixtureId = _fixtures
                .GroupBy(f => f.ElementId)
                .ToDictionary(g => g.Key, g => (g.First().ControlZone ?? "").Trim());
            _runsByZone = _zoneByFixtureId.Values
                .Where(z => z.Length > 0)
                .GroupBy(z => z, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var zoneResult = DmxZoneBuilder.Build(_fixtures, _loadedState.Clusters);
            ZoneNames = zoneResult.ZoneNames;
            UnassignedFixtures = zoneResult.UnassignedFixtures;

            // Preserve the designer's ticks across a refresh (by TypeId). On FIRST load, seed from the saved
            // curation if any (else all-selected so a Run works out of the box — the designer then unticks
            // what's not this job's kit). _savedDecoderTypeIds is consumed once, then refresh preserves live.
            var keepDecoders = DecoderRows.Count == 0
                ? _savedDecoderTypeIds
                : new HashSet<string>(DecoderRows.Where(r => r.IsSelected).Select(r => r.Candidate.TypeId));
            var keepDrivers = DriverRows.Count == 0
                ? _savedDriverTypeIds
                : new HashSet<string>(DriverRows.Where(r => r.IsSelected).Select(r => r.Candidate.TypeId));

            DecoderRows.Clear();
            foreach (var c in snapshot.DecoderCandidates)
                DecoderRows.Add(WireRow(new DmxDecoderRowViewModel(c, keepDecoders?.Contains(c.TypeId) ?? true)));

            DriverRows.Clear();
            foreach (var c in snapshot.DriverCandidates)
                DriverRows.Add(WireRow(new DmxDriverRowViewModel(c, keepDrivers?.Contains(c.TypeId) ?? true)));

            PruneClusters();
            RebuildLoopsAndPool();

            OnPropertyChanged(nameof(FixtureCount));
            OnPropertyChanged(nameof(ZoneCount));
            OnPropertyChanged(nameof(SummaryText));
        }

        /// <summary>Rebuild the loop tree + pool against the (possibly changed) zone set. On FIRST load the loop
        /// definitions come from the saved loops (consumed once); thereafter from the live rows so a Refresh
        /// keeps in-progress edits. Dropped zones fall out; a zone named in two loops sticks to the first
        /// (single-membership); leftovers form the pool.</summary>
        private void RebuildLoopsAndPool()
        {
            var defs = _initialLoops != null
                ? _initialLoops.OrderBy(l => l.Order)
                    .Select(l => (l.Name, Zones: (IEnumerable<string>)(l.ZoneValues ?? new List<string>()))).ToList()
                : Loops.Select(l => (l.Name, Zones: (IEnumerable<string>)l.AssignedZoneNames)).ToList();
            _initialLoops = null;

            var zoneSet = new HashSet<string>(ZoneNames, StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Loops.Clear();
            foreach (var d in defs)
            {
                var loop = WireLoop(new DmxLoopRowViewModel(d.Name));
                foreach (var zn in d.Zones)
                {
                    if (!zoneSet.Contains(zn) || used.Contains(zn)) continue;   // dropped or already in a loop
                    used.Add(zn);
                    loop.Zones.Add(MakeLoopZone(loop, zn));
                }
                Loops.Add(loop);
            }
            _loopSeq = Math.Max(_loopSeq, Loops.Count);

            RebuildPool();
        }

        // ── Cluster sub-builder (§8d): selection-driven, persisted by fixture ElementId ──────────────

        /// <summary>Drop cluster bindings to runs/zones that no longer exist (copied/deleted tape, retagged
        /// zones) so a refresh doesn't carry stale ids; empties land back in the residual on next solve.</summary>
        private void PruneClusters()
        {
            foreach (var c in _loadedState.Clusters)
                c.RunElementIds = (c.RunElementIds ?? new List<long>())
                    .Where(id => _zoneByFixtureId.TryGetValue(id, out var z)
                                 && string.Equals(z, c.ZoneValue, StringComparison.OrdinalIgnoreCase))
                    .Distinct().ToList();
            _loadedState.Clusters = _loadedState.Clusters.Where(c => c.RunElementIds.Count > 0).ToList();
        }

        /// <summary>(Re)populate a zone node's cluster rows from the persisted cluster DTOs for that zone.</summary>
        private void RefreshClusterRows(DmxLoopZoneViewModel z)
        {
            z.Clusters.Clear();
            foreach (var c in _loadedState.Clusters.Where(c =>
                         string.Equals(c.ZoneValue, z.ZoneName, StringComparison.OrdinalIgnoreCase)))
            {
                var row = new DmxClusterRowViewModel(c.ClusterId, c.Name, c.RunElementIds);
                WireClusterRow(z, row);
                z.Clusters.Add(row);
                _clusterSeq = Math.Max(_clusterSeq, ExtractSeq(c.Name));
            }
            z.RaiseResidualChanged();
        }

        private void WireClusterRow(DmxLoopZoneViewModel z, DmxClusterRowViewModel row)
        {
            row.VerifyCommand = new RelayCommand(() => Verify(row), () => CanCluster);
            row.AddSelectionCommand = new RelayCommand(() => AddSelectionToCluster(z, row), () => CanCluster);
            row.RemoveCommand = new RelayCommand(() => RemoveCluster(z, row), () => CanCluster);
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DmxClusterRowViewModel.Name)) RenameCluster(row); };
        }

        private static int ExtractSeq(string name)
        {
            var digits = new string((name ?? "").Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) ? n : 0;
        }

        private void NewClusterFromSelection(DmxLoopZoneViewModel z)
        {
            WithSelection(ids =>
            {
                var (mine, ignored) = FilterToZone(ids, z.ZoneName);
                if (mine.Count == 0) { BuilderStatus = $"Nothing in zone \"{z.ZoneName}\" selected ({ignored} ignored)."; return; }

                var dto = new DmxClusterDto
                {
                    ClusterId = Guid.NewGuid().ToString("N"),
                    Name = $"Cluster {++_clusterSeq}",
                    ZoneValue = z.ZoneName,
                    RunElementIds = new List<long>(),
                };
                _loadedState.Clusters.Add(dto);
                AssignRuns(z.ZoneName, dto.ClusterId, mine);
                BuilderStatus = $"New cluster: {mine.Count} run(s) in \"{z.ZoneName}\"" + IgnoredText(ignored);
                AfterClusterEdit(z);
            });
        }

        private void AddSelectionToCluster(DmxLoopZoneViewModel z, DmxClusterRowViewModel row)
        {
            WithSelection(ids =>
            {
                var (mine, ignored) = FilterToZone(ids, z.ZoneName);
                if (mine.Count == 0) { BuilderStatus = $"Nothing in zone \"{z.ZoneName}\" selected ({ignored} ignored)."; return; }
                AssignRuns(z.ZoneName, row.ClusterId, mine);
                BuilderStatus = $"Added {mine.Count} run(s) to \"{row.Name}\"" + IgnoredText(ignored);
                AfterClusterEdit(z);
            });
        }

        private void RemoveCluster(DmxLoopZoneViewModel z, DmxClusterRowViewModel row)
        {
            _loadedState.Clusters.RemoveAll(c => c.ClusterId == row.ClusterId);
            BuilderStatus = $"Removed \"{row.Name}\" — its runs returned to (unclustered).";
            AfterClusterEdit(z);
        }

        private void RenameCluster(DmxClusterRowViewModel row)
        {
            var dto = _loadedState.Clusters.FirstOrDefault(c => c.ClusterId == row.ClusterId);
            if (dto == null || dto.Name == row.Name) return;
            dto.Name = row.Name;
            Persist();
        }

        private void Verify(DmxClusterRowViewModel row)
        {
            var selection = _selection;
            if (selection == null || _workQueue == null) return;
            var ids = row.RunIds.ToList();
            _workQueue.Enqueue(() => { selection.Highlight(ids); return null!; }, _ => { });
        }

        /// <summary>Assign runs to one cluster, enforcing single-membership within the zone (last-wins).</summary>
        private void AssignRuns(string zone, string clusterId, IReadOnlyList<long> ids)
        {
            var set = new HashSet<long>(ids);
            foreach (var c in _loadedState.Clusters.Where(c =>
                         string.Equals(c.ZoneValue, zone, StringComparison.OrdinalIgnoreCase)))
            {
                if (c.ClusterId == clusterId)
                    c.RunElementIds = c.RunElementIds.Union(ids).Distinct().ToList();
                else
                    c.RunElementIds = c.RunElementIds.Where(id => !set.Contains(id)).ToList();
            }
            _loadedState.Clusters = _loadedState.Clusters.Where(c => c.RunElementIds.Count > 0).ToList();
        }

        private (List<long> Mine, int Ignored) FilterToZone(IReadOnlyList<long> ids, string zone)
        {
            var mine = new List<long>();
            int ignored = 0;
            foreach (var id in ids)
            {
                if (_zoneByFixtureId.TryGetValue(id, out var z)
                    && string.Equals(z, zone, StringComparison.OrdinalIgnoreCase)) mine.Add(id);
                else ignored++;
            }
            return (mine, ignored);
        }

        private static string IgnoredText(int ignored) =>
            ignored > 0 ? $" ({ignored} ignored — other zone / non-DMX)." : ".";

        /// <summary>Read the model selection on the Revit thread, then run <paramref name="onIds"/> on the UI thread.</summary>
        private void WithSelection(Action<IReadOnlyList<long>> onIds)
        {
            var selection = _selection;
            if (selection == null || _workQueue == null) return;
            _workQueue.Enqueue(() => (object)selection.GetSelectedIds(),
                               result => onIds((IReadOnlyList<long>)(result ?? new List<long>())));
        }

        /// <summary>After a cluster edit: repopulate the zone's rows, re-solve, persist.</summary>
        private void AfterClusterEdit(DmxLoopZoneViewModel z)
        {
            RefreshClusterRows(z);
            Run();
            Persist();
        }
    }
}
