#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Storage;

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

        /// <summary>
        /// The id of the project's store for <paramref name="schemaGuid"/>, or null when this project has none
        /// yet (or the schema was never loaded) — for a session lease's working set. A store created by a
        /// teammate and synced needs a borrow to overwrite; one that doesn't exist yet is created by its first
        /// save and owned by its creator, so it needs nothing.
        /// </summary>
        public static ElementId FindStorageId(Document doc, Guid schemaGuid)
        {
            var schema = Schema.Lookup(schemaGuid);
            return schema == null ? null : FindDataStorage(doc, schema)?.Id;
        }

        /// <summary>
        /// <b>The ExtensibleStorage save chokepoint.</b> Writes one entity of <paramref name="schema"/> into the
        /// project's store (creating it on first save) inside its own transaction, behind two refusals:
        /// <list type="number">
        /// <item>the storage-generation guard (<see cref="StorageGenerationService.AuthorizeWrite(Document, string)"/>) —
        /// a newer TurboSuite has saved this project, so writing here would split its data;</item>
        /// <item><paramref name="canOverwrite"/>, when given, checks the entity already stored — JSON stores pass a
        /// parse check so a payload this build can't read is never overwritten with an empty state.</item>
        /// </list>
        /// A refusal rolls back (writing nothing, stamp included) and returns <see cref="StorageSaveOutcome.Blocked"/>;
        /// the guard has already told the user, so callers report only <see cref="StorageSaveOutcome.RolledBack"/>
        /// (<see cref="WorksharingMessages.RolledBack"/>). Each save writes a fresh, whole entity — never a
        /// read-modify-write of the stored one.
        /// </summary>
        /// <param name="storeLabel">What the user would call this data, for the refusal notices — e.g.
        /// "TurboZones panel settings". Each store explains its own first refusal per document per session.</param>
        public static StorageSaveOutcome SaveEntity(Document doc, Schema schema, string storeLabel, string transactionName,
            Action<Entity> fill, Func<Entity, bool> canOverwrite = null)
        {
            using var tx = new Transaction(doc, transactionName);
            tx.Start();

            if (!StorageGenerationService.AuthorizeWrite(doc, storeLabel))
            {
                tx.RollBack();
                return StorageSaveOutcome.Blocked;
            }

            var existing = FindDataStorage(doc, schema);
            if (existing != null && canOverwrite != null)
            {
                var stored = existing.GetEntity(schema);
                if (stored.IsValid() && !canOverwrite(stored))
                {
                    tx.RollBack();
                    UnreadablePayloadNotice.ShowOnce(doc, storeLabel);
                    return StorageSaveOutcome.Blocked;
                }
            }

            var storage = existing ?? DataStorage.Create(doc);
            var entity = new Entity(schema);
            fill(entity);
            storage.SetEntity(entity);

            return CommitGuard.TryCommit(tx) ? StorageSaveOutcome.Saved : StorageSaveOutcome.RolledBack;
        }

        /// <summary>
        /// Carry-forward read for a store whose schema GUID was replaced (plan §2b): the current schema's store if
        /// the project has one, else the newest legacy schema's store present, else none. Legacy schemas are only
        /// <i>looked up</i> — a schema stored in a document registers when it opens (TurboSpike) — never built.
        /// Nothing is written: the first normal save creates the current store and leaves the legacy one in place.
        /// The caller maps a legacy entity with its own reader (old field names).
        /// </summary>
        public static (DataStorage Storage, Schema Schema, StoreChoice Choice) FindCurrentOrLegacy(
            Document doc, Guid currentGuid, IReadOnlyList<Guid> legacyNewestFirst)
        {
            var current = Schema.Lookup(currentGuid);
            var currentStore = current == null ? null : FindDataStorage(doc, current);

            var legacy = (legacyNewestFirst ?? Array.Empty<Guid>())
                .Select(g =>
                {
                    var s = Schema.Lookup(g);
                    return (Schema: s, Storage: s == null ? null : FindDataStorage(doc, s));
                })
                .ToList();

            var choice = StoreSourceChooser.Choose(currentStore != null, legacy.Select(l => l.Storage != null).ToList());
            return choice.Source switch
            {
                StoreSource.Current => (currentStore, current, choice),
                StoreSource.Legacy => (legacy[choice.LegacyIndex].Storage, legacy[choice.LegacyIndex].Schema, choice),
                _ => (null, null, choice)
            };
        }

        /// <summary>The document's path, or "" (an unsaved project, e.g. one just created from a template).</summary>
        internal static string DocPath(Document doc)
        {
            try { return doc.PathName ?? ""; }
            catch { return ""; }
        }

        /// <summary>Per-session key for "warn once per document": the path, else the title (a detached or
        /// unsaved project has no path yet).</summary>
        internal static string DocKey(Document doc)
        {
            string path = DocPath(doc);
            if (!string.IsNullOrEmpty(path)) return path;
            try { return doc.Title ?? ""; }
            catch { return ""; }
        }

        private static void WarnDuplicateOnce(Document doc, Schema schema)
        {
            string key = $"{DocKey(doc)}|{schema.GUID}";
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
    }
}
