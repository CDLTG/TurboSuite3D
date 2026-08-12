#nullable enable
using Autodesk.Revit.DB;
using TurboSuite.Dali.Persistence;

namespace TurboSuite.Dali.Services
{
    /// <summary>Shim-side <see cref="IDaliLoopStore"/> — persists the DALI tab's declared loops to the
    /// document's ExtensibleStorage via <see cref="DaliStorageService"/>. Called by <c>DaliTabViewModel</c>
    /// inside an <c>IRevitWorkQueue</c> work item, so the transaction runs on the Revit API thread.</summary>
    public sealed class DaliLoopStore : IDaliLoopStore
    {
        private readonly Document _doc;

        public DaliLoopStore(Document doc) => _doc = doc;

        public void Save(DaliModuleState state) => DaliStorageService.Save(_doc, state);
    }
}
