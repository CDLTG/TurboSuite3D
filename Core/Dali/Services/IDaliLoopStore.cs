#nullable enable
using TurboSuite.Dali.Persistence;

namespace TurboSuite.Dali.Services
{
    /// <summary>
    /// Revit-free contract for persisting TurboDALI's declared loops. Implemented shim-side
    /// (<c>DaliStorageService</c> against the active document) and invoked by <c>DaliTabViewModel</c> inside
    /// an <see cref="TurboSuite.Abstractions.IRevitWorkQueue"/> work item, so the ExtensibleStorage write
    /// runs on the Revit API thread — the same seam shape as <c>IPanelSettingsStore</c>.
    /// </summary>
    public interface IDaliLoopStore
    {
        /// <summary>Persist the declared loops. <b>Merge-preserving</b>: this write MUST NOT touch the
        /// persisted addressing <see cref="DaliModuleState.Snapshot"/> — the tab auto-saves loops on every
        /// edit and would otherwise wipe the numbering-lock baseline the main window owns. Only
        /// <c>state.Loops</c> (and the version) are taken; the stored Snapshot is read back and kept.</summary>
        void Save(DaliModuleState state);

        /// <summary>Persist just the addressing numbering-lock baseline (Lock / Re-lock / Unlock). The mirror
        /// of <see cref="Save"/>: it writes only the <see cref="DaliModuleState.Snapshot"/> and preserves the
        /// stored loops, so the two writers (tab loops vs. window lock) never clobber each other's field.</summary>
        void SaveSnapshot(DaliSnapshotDto? snapshot);
    }
}
