#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using TurboSuite.Abstractions;
using TurboSuite.Dmx.Input;
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
        private readonly DmxJobSettings _settings = new DmxJobSettings();

        private IReadOnlyList<DmxFixtureReading> _fixtures;
        private int _loopSeq;

        public DmxMainViewModel(DmxModelSnapshot snapshot,
                                IRevitWorkQueue? workQueue = null,
                                IDmxModelReader? reader = null)
        {
            _workQueue = workQueue;
            _reader = reader;

            Profiles = new ObservableCollection<DmxProfile>(DmxProfile.All);
            _selectedProfile = DmxProfile.Lutron;

            DecoderRows = new ObservableCollection<DmxDecoderRowViewModel>();
            DriverRows = new ObservableCollection<DmxDriverRowViewModel>();
            Loops = new ObservableCollection<DmxLoopRowViewModel>();
            ZoneNames = new List<string>();
            _bill = DmxBillViewModel.Empty("Run to compute the bill.");
            _fixtures = new List<DmxFixtureReading>();

            RunCommand = new RelayCommand(Run);
            AddLoopCommand = new RelayCommand(AddLoop, () => ZoneNames.Count > 0);
            RemoveLoopCommand = new RelayCommand<DmxLoopRowViewModel>(RemoveLoop);
            RefreshCommand = new RelayCommand(Refresh, () => _workQueue != null && _reader != null);

            LoadSnapshot(snapshot);
            Run();
        }

        // ── Declarations: profile ───────────────────────────────────────────────────────────────────
        public ObservableCollection<DmxProfile> Profiles { get; }

        private DmxProfile _selectedProfile;
        public DmxProfile SelectedProfile
        {
            get => _selectedProfile;
            set { if (SetProperty(ref _selectedProfile, value ?? DmxProfile.Lutron)) Run(); }
        }

        // ── Declarations: Kind-2 job settings (profile-seeded, overridable) ─────────────────────────
        public double SystemVolts { get => _settings.SystemVolts; set { _settings.SystemVolts = value; OnPropertyChanged(); } }
        public double BreakerAmps { get => _settings.BreakerAmps; set { _settings.BreakerAmps = value; OnPropertyChanged(); } }
        public double FeedVolts { get => _settings.FeedVolts; set { _settings.FeedVolts = value; OnPropertyChanged(); } }
        public double BreakerContinuousDerate { get => _settings.BreakerContinuousDerate; set { _settings.BreakerContinuousDerate = value; OnPropertyChanged(); } }
        public int MaxDriversPerBreaker { get => _settings.MaxDriversPerBreaker; set { _settings.MaxDriversPerBreaker = value; OnPropertyChanged(); } }
        public int MaxDevicesPerSegment { get => _settings.MaxDevicesPerSegment; set { _settings.MaxDevicesPerSegment = value; OnPropertyChanged(); } }
        public int ReservedChannels { get => _settings.ReservedChannels; set { _settings.ReservedChannels = value; OnPropertyChanged(); } }

        // ── Declarations: curated part pools + loops ────────────────────────────────────────────────
        public ObservableCollection<DmxDecoderRowViewModel> DecoderRows { get; }
        public ObservableCollection<DmxDriverRowViewModel> DriverRows { get; }
        public ObservableCollection<DmxLoopRowViewModel> Loops { get; }

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

        // ── Commands ─────────────────────────────────────────────────────────────────────────────────
        public ICommand RunCommand { get; }
        public ICommand AddLoopCommand { get; }
        public ICommand RemoveLoopCommand { get; }
        public ICommand RefreshCommand { get; }

        /// <summary>The pure solve (TurboDMX-Design §1.5 pipeline). Idempotent — safe to call constantly.</summary>
        public void Run()
        {
            var zoneResult = DmxZoneBuilder.Build(_fixtures);
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
                Bill = DmxBillViewModel.FromBill(bill, SelectedProfile.ChannelCeiling);
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

        private void AddLoop()
        {
            if (ZoneNames.Count == 0) return;
            Loops.Add(new DmxLoopRowViewModel($"Loop {++_loopSeq}", ZoneNames));
        }

        private void RemoveLoop(DmxLoopRowViewModel? loop)
        {
            if (loop != null && Loops.Remove(loop)) Run();
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

        // ── Snapshot load (preserving selections + loops across a refresh) ──────────────────────────
        private void LoadSnapshot(DmxModelSnapshot snapshot)
        {
            snapshot ??= new DmxModelSnapshot();
            _fixtures = snapshot.Fixtures;

            var zoneResult = DmxZoneBuilder.Build(_fixtures);
            ZoneNames = zoneResult.ZoneNames;
            UnassignedFixtures = zoneResult.UnassignedFixtures;

            // Preserve the designer's ticks across a refresh (by TypeId), defaulting to all-selected on
            // first load so a Run works out of the box (the designer then unticks what's not this job's kit).
            var keepDecoders = DecoderRows.Count == 0
                ? null
                : new HashSet<string>(DecoderRows.Where(r => r.IsSelected).Select(r => r.Candidate.TypeId));
            var keepDrivers = DriverRows.Count == 0
                ? null
                : new HashSet<string>(DriverRows.Where(r => r.IsSelected).Select(r => r.Candidate.TypeId));

            DecoderRows.Clear();
            foreach (var c in snapshot.DecoderCandidates)
                DecoderRows.Add(new DmxDecoderRowViewModel(c, keepDecoders?.Contains(c.TypeId) ?? true));

            DriverRows.Clear();
            foreach (var c in snapshot.DriverCandidates)
                DriverRows.Add(new DmxDriverRowViewModel(c, keepDrivers?.Contains(c.TypeId) ?? true));

            // Rebuild loops against the (possibly changed) zone set, preserving name + assignments by zone name.
            var prior = Loops.Select(l => (l.Name, Assigned: l.AssignedZoneNames)).ToList();
            Loops.Clear();
            foreach (var p in prior)
                Loops.Add(new DmxLoopRowViewModel(p.Name, ZoneNames, p.Assigned));

            OnPropertyChanged(nameof(FixtureCount));
            OnPropertyChanged(nameof(ZoneCount));
            OnPropertyChanged(nameof(SummaryText));
        }
    }
}
