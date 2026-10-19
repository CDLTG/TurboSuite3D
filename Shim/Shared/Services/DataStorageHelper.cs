#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Shared.Services
{
    /// <summary>
    /// Helpers for locating a project's <see cref="DataStorage"/> element by schema.
    /// Used by every storage service to read/write ExtensibleStorage entities.
    /// </summary>
    public static class DataStorageHelper
    {
        // (document path | schema GUID) pairs already warned about this session.
        private static readonly HashSet<string> _warnedDuplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The project's store for <paramref name="schema"/>, or null. Under worksharing two users can each create
        /// the same store before either syncs, leaving two <see cref="DataStorage"/>s with one schema. Picking the
        /// <b>lowest ElementId</b> makes every user read and write the same copy; the first time a duplicate is
        /// seen this session the user is warned once. The copies are not merged.
        /// </summary>
        public static DataStorage FindDataStorage(Document doc, Schema schema)
        {
            List<DataStorage> matches;
            using (var collector = new FilteredElementCollector(doc))
            {
                matches = collector
                    .OfClass(typeof(DataStorage))
                    .Cast<DataStorage>()
                    .Where(ds => ds.GetEntity(schema).IsValid())
                    .OrderBy(ds => ds.Id.ToRef().Value)
                    .ToList();
            }

            if (matches.Count > 1) WarnDuplicateOnce(doc, schema);
            return matches.FirstOrDefault();
        }

        private static void WarnDuplicateOnce(Document doc, Schema schema)
        {
            string key = $"{SafePath(doc)}|{schema.GUID}";
            if (!_warnedDuplicates.Add(key)) return;
            try
            {
                TaskDialog.Show("TurboSuite",
                    $"Duplicate TurboSuite data found for \"{schema.SchemaName}\" — the older copy is in use.\n\n" +
                    "This happens when two people first save the same TurboSuite setting before either syncs. " +
                    "Check the setting still looks right.");
            }
            catch { /* a warning must never break the read/write it rides on */ }
        }

        private static string SafePath(Document doc)
        {
            try { return doc.PathName ?? doc.Title ?? ""; }
            catch { return ""; }
        }
    }
}
