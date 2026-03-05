#nullable disable
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace TurboSuite.Shared.Services
{
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
