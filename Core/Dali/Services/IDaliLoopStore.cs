#nullable enable
using TurboSuite.Dali.Persistence;

namespace TurboSuite.Dali.Services
{
    /// <summary>
    /// Revit-free contract for persisting the DALI tab's declared loops (Phase 3e). Implemented shim-side
    /// (<c>DaliStorageService</c> against the active document) and invoked by <c>DaliTabViewModel</c> inside
    /// an <see cref="TurboSuite.Abstractions.IRevitWorkQueue"/> work item, so the ExtensibleStorage write
    /// runs on the Revit API thread — the same seam shape as <c>IPanelSettingsStore</c>.
    /// </summary>
    public interface IDaliLoopStore
    {
        void Save(DaliModuleState state);
    }
}
