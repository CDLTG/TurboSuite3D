#nullable disable
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace TurboSuite.Shared.Services
{
    /// <summary>
    /// Helpers for locating a project's <see cref="DataStorage"/> element by schema.
    /// Used by every storage service to read/write ExtensibleStorage entities.
    /// </summary>
    public static class DataStorageHelper
    {
        public static DataStorage FindDataStorage(Document doc, Schema schema)
        {
            using (var collector = new FilteredElementCollector(doc))
            {
                return collector
                    .OfClass(typeof(DataStorage))
                    .Cast<DataStorage>()
                    .FirstOrDefault(ds => ds.GetEntity(schema).IsValid());
            }
        }
    }
}
