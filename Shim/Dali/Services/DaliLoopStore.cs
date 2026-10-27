#nullable enable
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Dali.Persistence;
using TurboSuite.Shared.Services;

namespace TurboSuite.Dali.Services
{
    /// <summary>Shim-side <see cref="IDaliLoopStore"/> — persists the DALI declarations to the document's
    /// ExtensibleStorage via <see cref="DaliStorageService"/>. Called inside an <c>IRevitWorkQueue</c> work
    /// item, so the transaction runs on the Revit API thread.
    ///
    /// TurboDALI has <b>two</b> writers onto the one schema — the tab's loop auto-save and the window's
    /// numbering-lock — so both methods here <b>read-modify-write</b>: each loads the current persisted state,
    /// overwrites only its own field, and keeps the other's. Both run on the same single work queue, so the
    /// load/store pairs never interleave; the merge only has to survive ordering, which read-then-write does.
    ///
    /// <para><b>Worksharing:</b> the store is in TurboDALI's session lease (<see cref="DaliCommand"/>), so a
    /// rollback here is the rare implicit-borrow backstop — reported, never shown as saved.</para></summary>
    public sealed class DaliLoopStore : IDaliLoopStore
    {
        private readonly Document _doc;

        public DaliLoopStore(Document doc) => _doc = doc;

        /// <summary>Write the loops; preserve the persisted addressing Snapshot (the lock baseline).</summary>
        public void Save(DaliModuleState state)
        {
            var current = DaliStorageService.Load(_doc);
            current.Loops = state?.Loops ?? current.Loops;
            // Snapshot is left as loaded — the tab never authors it. Version rides up to 4 once a lock exists.
            current.PayloadVersion = current.Snapshot != null
                ? DaliPayload.CurrentVersion
                : (state?.PayloadVersion ?? current.PayloadVersion);
            ReportIfRolledBack(DaliStorageService.Save(_doc, current));
        }

        /// <summary>Write the numbering-lock baseline; preserve the persisted loops.</summary>
        public bool SaveSnapshot(DaliSnapshotDto? snapshot)
        {
            var current = DaliStorageService.Load(_doc);
            current.Snapshot = snapshot;
            current.PayloadVersion = DaliPayload.CurrentVersion;
            return ReportIfRolledBack(DaliStorageService.Save(_doc, current));
        }

        private static bool ReportIfRolledBack(bool committed)
        {
            if (!committed) TaskDialog.Show("TurboDALI", WorksharingMessages.RolledBack);
            return committed;
        }
    }
}
