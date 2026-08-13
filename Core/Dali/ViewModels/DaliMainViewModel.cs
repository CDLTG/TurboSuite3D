#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using TurboSuite.Abstractions;
using TurboSuite.Dali.Addressing;
using TurboSuite.Dali.Overlay;
using TurboSuite.Dali.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Dali.ViewModels
{
    /// <summary>
    /// The TurboDALI window's top ViewModel — wraps the loop-declaration <see cref="DaliTab"/> and adds the
    /// addressing surface (plan Phase 3): <b>Write addresses</b> (DALI's "Place" — writing the computed
    /// per-circuit identity into the model IS its placement) and the live Control-Zone color overlay.
    ///
    /// It stays Revit-free: the model read (<see cref="IDaliModelReader"/>), the param write
    /// (<see cref="IDaliAddressWriter"/>), and the overlay (<see cref="IDaliZoneColorService"/>) are seams the
    /// shim implements, all invoked through the <see cref="IRevitWorkQueue"/> so their transactions run on the
    /// Revit API thread. The pure <see cref="DaliAddressReconciler"/> turns the read + declared loops into the
    /// address map the writer stamps.
    ///
    /// This slice runs the reconcile <b>unlocked</b> (fresh numbering every write); the job-wide numbering
    /// lock + REVIEW surfacing (which freeze issued addresses) is the next slice — <see cref="Reviews"/> is
    /// already here so it lands with no window rework.
    /// </summary>
    public class DaliMainViewModel : ViewModelBase
    {
        private readonly IRevitWorkQueue _workQueue;
        private readonly IDaliModelReader _reader;
        private readonly IDaliAddressWriter _writer;
        private readonly IDaliZoneColorService _zoneColor;
        private readonly IDaliTabInputProvider? _inputProvider;

        public DaliMainViewModel(
            DaliTabViewModel tab,
            IRevitWorkQueue workQueue,
            IDaliModelReader reader,
            IDaliAddressWriter writer,
            IDaliZoneColorService zoneColor,
            IDaliTabInputProvider? inputProvider = null)
        {
            DaliTab = tab;
            _workQueue = workQueue;
            _reader = reader;
            _writer = writer;
            _zoneColor = zoneColor;
            _inputProvider = inputProvider;

            Reviews = new ObservableCollection<string>();
            WriteAddressesCommand = new RelayCommand(WriteAddresses, () => !_busy && DaliTab.Loops.Count > 0);
            RefreshCommand = new RelayCommand(Refresh, () => !_busy && _inputProvider != null);
        }

        public DaliTabViewModel DaliTab { get; }

        public ICommand WriteAddressesCommand { get; }

        public ICommand RefreshCommand { get; }

        /// <summary>REVIEW verdicts from the last write (empty while unlocked — populated once the lock lands).</summary>
        public ObservableCollection<string> Reviews { get; }

        public bool HasReviews => Reviews.Count > 0;

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

            // Declared loops, live from the tab — LoopId is the durable L# anchor, zones in declared order.
            var loops = DaliTab.Loops
                .Select(l => new DaliLoopInput(
                    l.LoopId, l.Name, l.Zones.Select(z => z.ZoneName).ToList()))
                .ToList();

            SetBusy(true);
            StatusText = "Reading circuits…";

            _workQueue.Enqueue(
                () =>
                {
                    var snapshot = _reader.Read();
                    // Unlocked fresh numbering: zone-block → NW-seeded spatial walk (baseline null, locked false).
                    var addressing = DaliAddressReconciler.Reconcile(loops, snapshot.Circuits, null, false);
                    string writeStatus = _writer.Write(addressing.TextByCircuit);
                    return (object)new WriteResult(addressing, writeStatus, snapshot.UncircuitedDaliCount);
                },
                result =>
                {
                    var wr = (WriteResult)result;

                    Reviews.Clear();
                    foreach (var r in wr.Addressing.Reviews) Reviews.Add(r.Message);
                    OnPropertyChanged(nameof(HasReviews));

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
                    SetBusy(false);
                    StatusText = "Refreshed from model.";
                    ApplyZoneColors();
                });
        }

        // ── Zone color overlay (H11) ────────────────────────────────────────────────────────────────────

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
    }
}
