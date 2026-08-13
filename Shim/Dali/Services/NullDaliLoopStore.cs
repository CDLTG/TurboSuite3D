#nullable enable
using TurboSuite.Dali.Persistence;

namespace TurboSuite.Dali.Services
{
    /// <summary>
    /// A no-op <see cref="IDaliLoopStore"/> — the <b>single-writer transition</b> (plan H6). While TurboDALI
    /// is live (gated on <c>ExperimentalCommandsEnabled</c>), it becomes the sole writer of the DALI schema;
    /// the still-present TurboZones DALI tab is handed this instead of the real <see cref="DaliLoopStore"/>,
    /// so its edits never persist and two writers can't disagree about the same GUID during dev.
    ///
    /// TurboZones still <i>reads</i> the persisted state for demand + placement (via
    /// <c>DaliDemandProvider</c> / <c>DaliPlacementMapper</c>, which hit <see cref="DaliStorageService"/>
    /// directly) — only the tab's write path is severed. The tab is removed outright when TurboDALI
    /// graduates (Phase 4), and this class retires with it.
    /// </summary>
    public sealed class NullDaliLoopStore : IDaliLoopStore
    {
        public void Save(DaliModuleState state) { /* single-writer transition: TurboDALI owns the writes */ }
    }
}
