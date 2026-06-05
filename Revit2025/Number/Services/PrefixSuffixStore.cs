#nullable disable
using Autodesk.Revit.DB;

namespace TurboSuite.Number.Services
{
    /// <summary>
    /// Shim-side <see cref="IPrefixSuffixStore"/> — binds the Revit-free contract to the
    /// active document and the static <see cref="RoomOrderStorageService"/>
    /// ExtensibleStorage helper. Must be invoked on the Revit API thread (via
    /// <see cref="RevitWorkQueue"/>).
    /// </summary>
    public class PrefixSuffixStore : IPrefixSuffixStore
    {
        private readonly Document _doc;

        public PrefixSuffixStore(Document doc)
        {
            _doc = doc;
        }

        public void Save(string prefix, string suffix)
            => RoomOrderStorageService.SavePrefixSuffix(_doc, prefix, suffix);
    }
}
