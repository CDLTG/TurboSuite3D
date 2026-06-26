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
using TurboSuite.Dmx.Persistence;
using TurboSuite.Dmx.Placement;
using TurboSuite.Dmx.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Dmx.ViewModels
{
    /// <summary>
    /// The TurboDMX window's ViewModel (TurboDMX-UI-Structure): the left "declarations" surface (profile,
    /// Kind-2 settings, curated decoder/driver pools, declared loops) and the right always-on "bill". The
    /// solve is the pure <see cref="DmxSolver"/> — Phase 1 makes ZERO model changes: the only Revit touch
    /// is the read (snapshot at open + optional Refresh through the work queue). Run is idempotent and
    /// recomputes the bill from current declarations; pre-solve gate refusals land in the bill as errors.
    /// </summary>
    public sealed class DmxMainViewModel : ViewModelBase
    {
        private readonly IRevitWorkQueue? _workQueue;
        private readonly IDmxModelReader? _reader;
        private readonly IDmxPlacementService? _placement;
        private readonly IDmxModelSelection? _selection;
        private readonly Action<DmxModuleState>? _persist;
        private readonly Func<string, bool>? _confirm;   // shim Yes/No gate for the destructive lock actions
        private readonly DmxJobSettings _settings = new DmxJobSettings();

        // Fixture ElementId → its Control Zone, so a model selection can be filtered to one zone's runs (§8d).
        private Dictionary<long, string> _zoneByFixtureId = new Dictionary<long, string>();
        private int _clusterSeq;

        // The last successful solve + its lock-aware numbering — kept so Place stamps the same DEC #s the bill
        // shows. Null whenever the current declarations don't solve (empty/guidance/gate error).
        private DmxBill? _lastBill;
        private DmxNumbering? _lastNumbering;
        private bool _placing;   // guard against re-entrant Place while a pick is open

        // The last-loaded module state — preserved so a save round-trips the overlays this VM does NOT yet
        // manage (clusters, control-system tags, the solve snapshot); BuildState() overwrites only Settings
        // + Loops and carries the rest through untouched.
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
                                IDmxModelSelection? selection = null)
        {
            _workQueue = workQueue;
            _reader = reader;
            _placement = placement;
            _selection = selection;
            _persist = persist;
            _confirm = confirm;

            Profiles = new ObservableCollection<DmxProfile>(DmxProfile.All);
            _selectedProfile = DmxProfile.Lutron;

            DecoderRows = new ObservableCollection<DmxDecoderRowViewModel>();
            DriverRows = new ObservableCollection<DmxDriverRowViewModel>();
            Loops = new ObservableCollection<DmxLoopRowViewModel>();
            ZoneClusterGroups = new ObservableCollection<DmxZoneClusterGroupViewModel>();
            ZoneNames = new List<string>();
            _bill = DmxBillViewModel.Empty("Run to compute the bill.");
            _fixtures = new List<DmxFixtureReading>();

            RunCommand = new RelayCommand(Run);
            AddLoopCommand = new RelayCommand(AddLoop, () => ZoneNames.Count > 0);
            RemoveLoopCommand = new RelayCommand<DmxLoopRowViewModel>(RemoveLoop);
            RefreshCommand = new RelayCommand(Refresh, () => _workQueue != null && _reader != null);
            PlaceCommand = new RelayCommand(Place, CanPlace);
            // One button does both: Lock (first time) and Re-lock (re-baseline when already Locked).
            LockCommand = new RelayCommand(Lock, () => _lastNumbering != null);
            UnlockCommand = new RelayCommand(Unlock, () => IsLocked);

            ApplyPersistedState(state);
            LoadSnapshot(snapshot);
            Run();
            _loaded = true;   // any later mutation now persists
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

        // ── Declarations: curated part pools + loops ────────────────────────────────────────────────
        public ObservableCollection<DmxDecoderRowViewModel> DecoderRows { get; }
        public ObservableCollection<DmxDriverRowViewModel> DriverRows { get; }
        public ObservableCollection<DmxLoopRowViewModel> Loops { get; }

        // ── Cluster sub-builder (§8d) — one editor group per zone, residual visible ─────────────────
        public ObservableCollection<DmxZoneClusterGroupViewModel> ZoneClusterGroups { get; }

        private string _clusterStatus = "";
        public string ClusterStatus { get => _clusterStatus; private set => SetProperty(ref _clusterStatus, value); }

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

        // ── Placement status (right of the bill / footer) ───────────────────────────────────────────
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
        public ICommand AddLoopCommand { get; }
        public ICommand RemoveLoopCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand PlaceCommand { get; }
        public ICommand LockCommand { get; }
        public ICommand UnlockCommand { get; }

        /// <summary>The pure solve (TurboDMX-Design §1.5 pipeline). Idempotent — safe to call constantly.</summary>
        public void Run()
        {
            _lastBill = null; _lastNumbering = null;   // invalidate the placement plan until a clean solve below

            var zoneResult = DmxZoneBuilder.Build(_fixtures, _loadedState.Clusters);
            if (zoneResult.Zones.Count == 0)
            {
                Bill = DmxBillViewModel.Empty("No DMX fixtures with a Control Zone assigned.");
                RaisePlaceCanExecute();
                return;
            }

            var decoders = DecoderRows.Where(r => r.IsSelected).Select(r => r.Candidate).ToList();
            if (decoders.Count == 0) { Bill = DmxBillViewModel.Empty("Tick at least one decoder type."); RaisePlaceCanExecute(); return; }

            var drivers = DriverRows.Where(r => r.IsSelected).Select(r => r.Candidate).ToList();
            if (drivers.Count == 0) { Bill = DmxBillViewModel.Empty("Tick at least one driver type."); RaisePlaceCanExecute(); return; }

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

            RaisePlaceCanExecute();
        }

        // ── Place (BuildPlan Phase 2: loop-by-loop click-to-place of the solved system) ──────────────
        private bool CanPlace() =>
            _placement != null && _workQueue != null && !_placing
            && _lastBill != null && _lastNumbering != null && _lastBill.TotalDecoders > 0;

        /// <summary>Build the placement plan off the last solve and hand it to the shim through the work
        /// queue (picks + writes run on the Revit API thread). The decoder/driver type NAMES are mapped back
        /// to the curated pool's loaded-family identities so the shim drops the exact ticked types.</summary>
        private void Place()
        {
            if (!CanPlace()) return;

            var decoderMap = DecoderRows.Where(r => r.IsSelected)
                .GroupBy(r => r.Candidate.Name).ToDictionary(g => g.Key, g => g.First().Candidate.TypeId);
            var driverMap = DriverRows.Where(r => r.IsSelected)
                .GroupBy(r => r.Candidate.Name).ToDictionary(g => g.Key, g => g.First().Candidate.TypeId);

            var plan = DmxPlacementPlanner.Build(_lastBill!, _lastNumbering!, decoderMap, driverMap);
            var registry = _loadedState.Placed
                .Select(p => new DmxPlacedPair(p.Dec, p.DecoderId, p.DriverId)).ToList();
            bool locked = IsLocked;

            _placing = true;
            RaisePlaceCanExecute();
            PlacementStatus = "Placing… pick a point for each loop in the model (Esc to stop).";

            _workQueue!.Enqueue(
                () => _placement!.Place(plan, locked, registry),
                result =>
                {
                    _placing = false;
                    if (result is DmxPlacementResult r)
                    {
                        MergeRegistry(r);
                        PlacementStatus = r.Summary;
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

        private void AddLoop()
        {
            if (ZoneNames.Count == 0) return;
            var loop = new DmxLoopRowViewModel($"Loop {++_loopSeq}", ZoneNames);
            WireLoop(loop);
            Loops.Add(loop);
            Persist();
        }

        private void RemoveLoop(DmxLoopRowViewModel? loop)
        {
            if (loop != null && Loops.Remove(loop)) { Run(); Persist(); }
        }

        /// <summary>Re-read the model on the Revit thread (work queue), then rebuild + re-solve. No-op when
        /// constructed without a reader (e.g. unit tests).</summary>
        public void Refresh()
        {
            if (_workQueue == null || _reader == null) return;
            _workQueue.Enqueue(
                () => _reader.Read(),
                result => { LoadSnapshot((DmxModelSnapshot)result); Run(); });
        }

        // ── Persistence (doc-side ExtensibleStorage via the injected save callback) ─────────────────
        //
        // Every persisted declaration — profile, Kind-2 settings, curated part-pool ticks, declared loops —
        // funnels through Persist() on change. The injected callback (shim-side) coalesces and writes the
        // bundle to the DMX schema on the Revit thread (BuildPlan Phase 2 loop-persistence). No-op until the
        // constructor finishes (_loaded) so the initial load doesn't write the model back to itself, and a
        // no-op when constructed without a persister (unit tests).

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
            _settings.BreakerBasis = Enum.TryParse<BreakerBasis>(s.BreakerBasis, out var basis)
                ? basis : BreakerBasis.ConnectedLoad;

            // Empty saved list ⇒ never curated ⇒ leave null so LoadSnapshot defaults to all-selected.
            _savedDecoderTypeIds = s.DecoderTypeIds?.Count > 0 ? new HashSet<string>(s.DecoderTypeIds) : null;
            _savedDriverTypeIds = s.DriverTypeIds?.Count > 0 ? new HashSet<string>(s.DriverTypeIds) : null;
            _initialLoops = _loadedState.Loops?.Count > 0 ? _loadedState.Loops : null;
        }

        /// <summary>Build the current declarations into a <see cref="DmxModuleState"/>, carrying through the
        /// overlays this VM doesn't manage yet (clusters, control-system tags, snapshot) from the last load.</summary>
        private DmxModuleState BuildState()
        {
            var state = _loadedState;   // preserve PayloadVersion + the unmanaged overlays
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

        /// <summary>Subscribe a loop's name + its zone ticks to persistence.</summary>
        private DmxLoopRowViewModel WireLoop(DmxLoopRowViewModel loop)
        {
            loop.PropertyChanged += (_, __) => Persist();
            foreach (var z in loop.Zones)
                z.PropertyChanged += (_, __) => Persist();
            return loop;
        }

        // ── Snapshot load (preserving selections + loops across a refresh) ──────────────────────────
        private void LoadSnapshot(DmxModelSnapshot snapshot)
        {
            snapshot ??= new DmxModelSnapshot();
            _fixtures = snapshot.Fixtures;
            _zoneByFixtureId = _fixtures
                .GroupBy(f => f.ElementId)
                .ToDictionary(g => g.Key, g => (g.First().ControlZone ?? "").Trim());

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

            // Rebuild loops against the (possibly changed) zone set, preserving name + assignments by zone
            // name. On FIRST load they come from the saved loops (_initialLoops, consumed once); thereafter
            // from the live rows so a Refresh keeps in-progress edits.
            var prior = _initialLoops != null
                ? _initialLoops.OrderBy(l => l.Order)
                    .Select(l => (l.Name, Assigned: (IEnumerable<string>)l.ZoneValues)).ToList()
                : Loops.Select(l => (l.Name, Assigned: (IEnumerable<string>)l.AssignedZoneNames)).ToList();
            _initialLoops = null;

            Loops.Clear();
            foreach (var p in prior)
                Loops.Add(WireLoop(new DmxLoopRowViewModel(p.Name, ZoneNames, p.Assigned)));
            _loopSeq = Math.Max(_loopSeq, Loops.Count);

            PruneClusters();
            RebuildClusterGroups();

            OnPropertyChanged(nameof(FixtureCount));
            OnPropertyChanged(nameof(ZoneCount));
            OnPropertyChanged(nameof(SummaryText));
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

        private void RebuildClusterGroups()
        {
            ZoneClusterGroups.Clear();
            _clusterSeq = 0;
            var runsByZone = _zoneByFixtureId.Values
                .Where(z => z.Length > 0)
                .GroupBy(z => z, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (var zone in ZoneNames)
            {
                runsByZone.TryGetValue(zone, out int total);
                if (total < 2) continue;   // a single-run zone can't be split — no cluster UI (flat default)

                var group = new DmxZoneClusterGroupViewModel(zone, total);
                group.NewClusterCommand = new RelayCommand(() => NewClusterFromSelection(group), () => CanCluster);

                foreach (var c in _loadedState.Clusters.Where(c =>
                             string.Equals(c.ZoneValue, zone, StringComparison.OrdinalIgnoreCase)))
                {
                    var row = new DmxClusterRowViewModel(c.ClusterId, c.Name, c.RunElementIds);
                    WireClusterRow(group, row);
                    group.Clusters.Add(row);
                    _clusterSeq = Math.Max(_clusterSeq, ExtractSeq(c.Name));
                }
                ZoneClusterGroups.Add(group);
            }
        }

        private void WireClusterRow(DmxZoneClusterGroupViewModel group, DmxClusterRowViewModel row)
        {
            row.VerifyCommand = new RelayCommand(() => Verify(row), () => CanCluster);
            row.AddSelectionCommand = new RelayCommand(() => AddSelectionToCluster(group, row), () => CanCluster);
            row.RemoveCommand = new RelayCommand(() => RemoveCluster(group, row), () => CanCluster);
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DmxClusterRowViewModel.Name)) RenameCluster(row); };
        }

        private static int ExtractSeq(string name)
        {
            var digits = new string((name ?? "").Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) ? n : 0;
        }

        private void NewClusterFromSelection(DmxZoneClusterGroupViewModel group)
        {
            WithSelection(ids =>
            {
                var (mine, ignored) = FilterToZone(ids, group.ZoneName);
                if (mine.Count == 0) { ClusterStatus = $"Nothing in zone \"{group.ZoneName}\" selected ({ignored} ignored)."; return; }

                var dto = new DmxClusterDto
                {
                    ClusterId = Guid.NewGuid().ToString("N"),
                    Name = $"Cluster {++_clusterSeq}",
                    ZoneValue = group.ZoneName,
                    RunElementIds = new List<long>(),
                };
                _loadedState.Clusters.Add(dto);
                AssignRuns(group.ZoneName, dto.ClusterId, mine);
                ClusterStatus = $"New cluster: {mine.Count} run(s) in \"{group.ZoneName}\"" + IgnoredText(ignored);
                AfterClusterEdit();
            });
        }

        private void AddSelectionToCluster(DmxZoneClusterGroupViewModel group, DmxClusterRowViewModel row)
        {
            WithSelection(ids =>
            {
                var (mine, ignored) = FilterToZone(ids, group.ZoneName);
                if (mine.Count == 0) { ClusterStatus = $"Nothing in zone \"{group.ZoneName}\" selected ({ignored} ignored)."; return; }
                AssignRuns(group.ZoneName, row.ClusterId, mine);
                ClusterStatus = $"Added {mine.Count} run(s) to \"{row.Name}\"" + IgnoredText(ignored);
                AfterClusterEdit();
            });
        }

        private void RemoveCluster(DmxZoneClusterGroupViewModel group, DmxClusterRowViewModel row)
        {
            _loadedState.Clusters.RemoveAll(c => c.ClusterId == row.ClusterId);
            ClusterStatus = $"Removed \"{row.Name}\" — its runs returned to (unclustered).";
            AfterClusterEdit();
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

        private void AfterClusterEdit()
        {
            RebuildClusterGroups();
            Run();
            Persist();
        }
    }
}
