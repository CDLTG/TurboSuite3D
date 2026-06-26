#nullable disable
using Autodesk.Revit.DB;
using TurboSuite.Abstractions;
using TurboSuite.Dmx.Persistence;

namespace TurboSuite.Dmx.Services
{
    /// <summary>
    /// Coalescing bridge between the Core ViewModel's <c>Persist(DmxModuleState)</c> callback and the
    /// doc-side <see cref="DmxStorageService"/> write. The VM fires on every declaration change (every
    /// keystroke in a settings field, every loop tick); this collapses a burst into a single ES transaction
    /// per drain by keeping only the latest state and re-firing once after a write if a newer state arrived
    /// while it was in flight — the modeless coalesced-save pattern (CLAUDE.md WPF Patterns / TurboZones).
    ///
    /// All ES writes run on the Revit API thread through the shared <see cref="IRevitWorkQueue"/>; Save() and
    /// the completion callback both run on the WPF thread, so the small _lock is just defensive.
    /// </summary>
    public sealed class DmxStatePersister
    {
        private readonly IRevitWorkQueue _queue;
        private readonly Document _doc;
        private readonly object _lock = new object();
        private DmxModuleState _pending;
        private bool _inFlight;

        public DmxStatePersister(IRevitWorkQueue queue, Document doc)
        {
            _queue = queue;
            _doc = doc;
        }

        /// <summary>Queue the latest declarations for persistence (coalesced).</summary>
        public void Save(DmxModuleState state)
        {
            lock (_lock)
            {
                _pending = state;
                if (_inFlight) return;   // a write is running; it will pick up this state on completion
                _inFlight = true;
            }
            Flush();
        }

        private void Flush()
        {
            DmxModuleState toWrite;
            lock (_lock) { toWrite = _pending; _pending = null; }
            if (toWrite == null) { lock (_lock) { _inFlight = false; } return; }

            _queue.Enqueue(
                () => { DmxStorageService.Save(_doc, toWrite); return null; },
                _ =>
                {
                    bool more;
                    lock (_lock) { more = _pending != null; if (!more) _inFlight = false; }
                    if (more) Flush();   // a newer state landed mid-write — persist it too
                });
        }
    }
}
