#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using TurboSuite.Abstractions;
using TurboSuite.Dali.Addressing;
using TurboSuite.Dali.Overlay;
using TurboSuite.Dali.Persistence;
using TurboSuite.Dali.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Dali.ViewModels
{
    /// <summary>
    /// The TurboDALI window's top ViewModel — wraps the loop-declaration <see cref="DaliTab"/> and adds the
    /// addressing surface: <b>Write addresses</b> (DALI's "Place" — writing the computed
    /// per-circuit identity into the model IS its placement) and the live Control-Zone color overlay.
    ///
    /// It stays Revit-free: the model read (<see cref="IDaliModelReader"/>), the param write
    /// (<see cref="IDaliAddressWriter"/>), and the overlay (<see cref="IDaliZoneColorService"/>) are seams the
    /// shim implements, all invoked through the <see cref="IRevitWorkQueue"/> so their transactions run on the
    /// Revit API thread. The pure <see cref="DaliAddressReconciler"/> turns the read + declared loops into the
    /// address map the writer stamps.
    ///
    /// <b>Numbering lock (job-wide).</b> Writes run lock-aware: <b>Unlocked</b> churns freely (fresh numbering
    /// every write, from the live spatial walk); <b>Locked</b> freezes every issued <c>L#-##</c> against the
    /// persisted <see cref="DaliSnapshotDto"/> baseline and only appends. <see cref="LockCommand"/> captures
    /// (or re-baselines) that snapshot; <see cref="UnlockCommand"/> discards it. The two write to their own
    /// field of the shared schema via <see cref="IDaliLoopStore.SaveSnapshot"/>, which the tab's loop
    /// auto-save preserves (merge-preserving store), so the two writers never clobber each other.
    /// </summary>
    public class DaliMainViewModel : ViewModelBase
    {
        private readonly IRevitWorkQueue _workQueue;
        private readonly IDaliModelReader _reader;
        private readonly IDaliAddressWriter _writer;
        private readonly IDaliZoneColorService _zoneColor;
        private readonly IDaliLoopStore _store;
        private readonly IDaliTabInputProvider? _inputProvider;
        private readonly Func<string, bool>? _confirm;   // shim Yes/No gate for the destructive lock actions

        /// <summary>The persisted addressing baseline + lock state, held in the window. Null / "Unlocked" until
        /// a Lock captures it. Mirrors <c>DmxMainViewModel._loadedState.Snapshot</c>.</summary>
        private DaliSnapshotDto? _snapshot;

        public DaliMainViewModel(
            DaliTabViewModel tab,
            IRevitWorkQueue workQueue,
            IDaliModelReader reader,
            IDaliAddressWriter writer,
            IDaliZoneColorService zoneColor,
            IDaliLoopStore store,
            IDaliTabInputProvider? inputProvider = null,
            DaliModuleState? saved = null,
            Func<string, bool>? confirm = null)
        {
            DaliTab = tab;
            _workQueue = workQueue;
            _reader = reader;
            _writer = writer;
            _zoneColor = zoneColor;
            _store = store;
            _inputProvider = inputProvider;
            _confirm = confirm;
            _snapshot = saved?.Snapshot;

            Reviews = new ObservableCollection<string>();
            WriteAddressesCommand = new RelayCommand(WriteAddresses, () => !_busy && DaliTab.Loops.Count > 0);
            RefreshCommand = new RelayCommand(Refresh, () => !_busy && _inputProvider != null);
            LockCommand = new RelayCommand(Lock, () => !_busy && DaliTab.Loops.Count > 0);
            UnlockCommand = new RelayCommand(Unlock, () => !_busy && IsLocked);
        }

        public DaliTabViewModel DaliTab { get; }

        public ICommand WriteAddressesCommand { get; }

        public ICommand RefreshCommand { get; }

        public ICommand LockCommand { get; }

        public ICommand UnlockCommand { get; }

        /// <summary>REVIEW verdicts from the last locked write (empty while unlocked).</summary>
        public ObservableCollection<string> Reviews { get; }

        public bool HasReviews => Reviews.Count > 0;

        // ── Lock state (job-wide) ─────────────────────────────────────────────────────────────────────────

        public bool IsLocked =>
            string.Equals(_snapshot?.NumberingState, "Locked", StringComparison.OrdinalIgnoreCase);

        /// <summary>The lock-banner text — always shown (grey while unlocked, amber when Locked). Mirrors
        /// TurboDMX's wording (issued values preserved, conflicts flag REVIEW), with "addresses" for DALI.</summary>
        public string LockStateText => IsLocked
            ? "LOCKED — issued addresses preserved; conflicts flag REVIEW"
            : "UNLOCKED — numbering re-derives freely each run";

        /// <summary>The Lock button's label — "Lock" first time, "Re-lock" once locked (re-baseline).</summary>
        public string LockButtonText => IsLocked ? "Re-lock" : "Lock";

        private bool _busy;

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        // ── Write addresses ─────────────────────────────────────────────────────────────────────────────

        private void WriteAddresses()
        {
            if (_busy) return;

            var loops = BuildLoopInputs();
            var baseline = _snapshot;
            bool locked = IsLocked;

            SetBusy(true);
            StatusText = "Reading circuits…";

            _workQueue.Enqueue(
                () =>
                {
                    var snapshot = _reader.Read();
                    // Lock-aware: unlocked ⇒ fresh (zone-block → NW-seeded walk); locked ⇒ pin to the baseline
                    // and only append past the loop high-water, flagging moved/retired issued addresses.
                    var addressing = DaliAddressReconciler.Reconcile(loops, snapshot.Circuits, baseline, locked);
                    string writeStatus = _writer.Write(addressing.TextByCircuit);
                    return (object)new WriteResult(addressing, writeStatus, snapshot.UncircuitedDaliCount);
                },
                result =>
                {
                    var wr = (WriteResult)result;

                    ShowReviews(wr.Addressing);

                    int circuits = wr.Addressing.TextByCircuit.Count;
                    string msg = $"Wrote addresses for {circuits} DALI circuit{(circuits == 1 ? "" : "s")}.";
                    if (!string.IsNullOrEmpty(wr.WriteStatus)) msg = wr.WriteStatus;
                    if (wr.UncircuitedDaliCount > 0)
                        msg += $"  ({wr.UncircuitedDaliCount} uncircuited DALI fixture"
                             + (wr.UncircuitedDaliCount == 1 ? "" : "s") + " skipped — circuit them to address.)";
                    StatusText = msg;

                    SetBusy(false);
                });
        }

        /// <summary>Declared loops, live from the tab — <c>LoopId</c> is the durable L# anchor, zones in
        /// declared order (the outer key of the canonical load ordering).</summary>
        private List<DaliLoopInput> BuildLoopInputs() =>
            DaliTab.Loops
                .Select(l => new DaliLoopInput(l.LoopId, l.Name, l.Zones.Select(z => z.ZoneName).ToList()))
                .ToList();

        private void ShowReviews(DaliAddressing addressing)
        {
            Reviews.Clear();
            foreach (var r in addressing.Reviews) Reviews.Add(r.Message);
            OnPropertyChanged(nameof(HasReviews));
        }

        // ── Numbering lock lifecycle: Unlocked ⇄ Locked (mirrors DmxMainViewModel) ────────────────────────
        // Lock does double duty: the first press captures the current numbering as the frozen baseline;
        // pressing it while already locked RE-baselines (folds any post-lock appends into the issued set,
        // without renumbering). Unlock discards the baseline back to churn-freely. Both persist the snapshot
        // through the merge-preserving store, so the tab's loop auto-save leaves the baseline untouched.

        private void Lock()
        {
            if (_busy) return;

            // Already locked ⇒ deliberate re-baseline; gate it (issued addresses are at stake).
            if (IsLocked && !(_confirm?.Invoke(
                    "Re-lock DALI numbering to a NEW baseline?\n\n"
                    + "The current L#-## addresses (including any circuits added since the last lock) become the "
                    + "new issued baseline, and any REVIEW flags clear. The addresses themselves don't change. "
                    + "Only do this for a sanctioned re-issue.") ?? true)) return;

            var loops = BuildLoopInputs();
            var baseline = _snapshot;
            bool wasLocked = IsLocked;

            SetBusy(true);
            StatusText = "Locking numbering…";

            _workQueue.Enqueue(
                () =>
                {
                    var snapshot = _reader.Read();
                    // Reconcile against the current state (fresh on first lock, pinned on re-lock so numbers
                    // don't move), write it, then freeze exactly what was written as the new baseline.
                    var addressing = DaliAddressReconciler.Reconcile(loops, snapshot.Circuits, baseline, wasLocked);
                    string writeStatus = _writer.Write(addressing.TextByCircuit);
                    var captured = DaliSnapshotBuilder.Capture(addressing, "Locked");
                    _store.SaveSnapshot(captured);
                    return (object)new LockResult(addressing, captured, writeStatus, snapshot.UncircuitedDaliCount);
                },
                result =>
                {
                    var lr = (LockResult)result;
                    _snapshot = lr.Snapshot;
                    OnLockChanged();
                    ShowReviews(lr.Addressing);   // a re-baseline clears them; a fresh lock has none

                    int circuits = lr.Addressing.TextByCircuit.Count;
                    StatusText = $"Numbering locked — {circuits} address{(circuits == 1 ? "" : "es")} frozen.";
                    SetBusy(false);
                });
        }

        private void Unlock()
        {
            if (_busy || !IsLocked) return;
            if (!(_confirm?.Invoke(
                    "Unlock DALI numbering?\n\n"
                    + "Writes will renumber freely from the spatial walk and the issued baseline is discarded. "
                    + "Use this only before the numbering lockdown point.") ?? true)) return;

            SetBusy(true);
            StatusText = "Unlocking numbering…";

            _workQueue.Enqueue(
                () =>
                {
                    var cleared = new DaliSnapshotDto { NumberingState = "Unlocked" };
                    _store.SaveSnapshot(cleared);
                    return (object)cleared;
                },
                result =>
                {
                    _snapshot = (DaliSnapshotDto)result;
                    Reviews.Clear();
                    OnPropertyChanged(nameof(HasReviews));
                    OnLockChanged();
                    StatusText = "Numbering unlocked — the next write renumbers freely.";
                    SetBusy(false);
                });
        }

        private void OnLockChanged()
        {
            OnPropertyChanged(nameof(IsLocked));
            OnPropertyChanged(nameof(LockStateText));
            OnPropertyChanged(nameof(LockButtonText));
            CommandManager.InvalidateRequerySuggested();
        }

        // ── Refresh ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Re-read the model on the API thread (work queue), reseed the pool + counts + loops, and
        /// re-apply the zone overlay — the TurboDMX Refresh gesture. No-op without an input provider (tests).</summary>
        private void Refresh()
        {
            if (_busy || _inputProvider == null) return;

            SetBusy(true);
            StatusText = "Refreshing…";

            _workQueue.Enqueue(
                () => (object)_inputProvider.Read(),
                result =>
                {
                    var inputs = (DaliTabInputs)result;
                    DaliTab.Reseed(inputs.Zones, inputs.PanelZones, inputs.Saved);
                    // Re-sync the lock state from the fresh read — another session (or a re-open) may have
                    // locked/unlocked since this window opened.
                    _snapshot = inputs.Saved?.Snapshot;
                    OnLockChanged();
                    SetBusy(false);
                    StatusText = "Refreshed from model.";
                    ApplyZoneColors();
                });
        }

        // ── Zone color overlay ──────────────────────────────────────────────────────────────────────────

        /// <summary>Apply the active-view zone overlay (on open + after each write). Non-fatal: a
        /// view-type-lockout note lands in the status line.</summary>
        public void ApplyZoneColors()
        {
            var zoneNames = DaliTab.Pool.Select(z => z.ZoneName)
                .Concat(DaliTab.Loops.SelectMany(l => l.Zones.Select(z => z.ZoneName)))
                .ToList();
            var palette = DaliZonePalette.Build(zoneNames);
            if (palette.Count == 0) return;

            _workQueue.Enqueue(
                () => (object)_zoneColor.Apply(palette),
                status => { if (!string.IsNullOrEmpty((string)status)) StatusText = (string)status; });
        }

        /// <summary>Revert the overlay on window close, then run <paramref name="onReverted"/> on the UI
        /// thread (the deferred-close handshake, mirroring TurboDMX).</summary>
        public void RevertZoneColors(Action onReverted)
        {
            _workQueue.Enqueue(
                () => (object)_zoneColor.Revert(),
                _ => onReverted?.Invoke());
        }

        // ── Busy gate ───────────────────────────────────────────────────────────────────────────────────

        private void SetBusy(bool busy)
        {
            _busy = busy;
            // The write completes while a Revit dialog/view may hold focus, so RequerySuggested won't fire on
            // its own — nudge it so the button re-enables (the modeless CanExecute-staleness rule).
            CommandManager.InvalidateRequerySuggested();
        }

        private sealed class WriteResult
        {
            public WriteResult(DaliAddressing addressing, string writeStatus, int uncircuitedDaliCount)
            {
                Addressing = addressing;
                WriteStatus = writeStatus;
                UncircuitedDaliCount = uncircuitedDaliCount;
            }

            public DaliAddressing Addressing { get; }
            public string WriteStatus { get; }
            public int UncircuitedDaliCount { get; }
        }

        private sealed class LockResult
        {
            public LockResult(DaliAddressing addressing, DaliSnapshotDto snapshot, string writeStatus,
                              int uncircuitedDaliCount)
            {
                Addressing = addressing;
                Snapshot = snapshot;
                WriteStatus = writeStatus;
                UncircuitedDaliCount = uncircuitedDaliCount;
            }

            public DaliAddressing Addressing { get; }
            public DaliSnapshotDto Snapshot { get; }
            public string WriteStatus { get; }
            public int UncircuitedDaliCount { get; }
        }
    }
}
