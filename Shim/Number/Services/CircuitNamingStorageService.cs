#nullable disable
using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using TurboSuite.Shared.Services;

namespace TurboSuite.Number.Services
{
    /// <summary>
    /// TurboNumber-local ExtensibleStorage: the power-supply numbering prefix/suffix and
    /// the keypad "sort by room" toggle. The project-wide room order itself now lives in
    /// <see cref="TurboSuite.Shared.Services.RoomOrderStorageService"/>.
    ///
    /// The bool schema (GUID/name/field unchanged) was formerly "sidebar visible"; the
    /// sidebar is now permanent, so the same stored bool is repurposed to persist the
    /// keypad tab's room-sort toggle. Reusing the schema verbatim is deliberate — no new
    /// GUID per the ExtensibleStorage rule, and the old value maps cleanly (the sidebar
    /// flag also gated column sort before).
    /// </summary>
    public static class CircuitNamingStorageService
    {
        private static readonly Guid KeypadSortSchemaGuid = new Guid("b2e4c8d3-5f7a-4b9c-8d1e-3f6a9b0c2d4e");
        private const string KeypadSortSchemaName = "TurboNumberSidebarState";
        private const string KeypadSortFieldName = "IsSidebarVisible";

        private static readonly Guid PrefixSuffixSchemaGuid = new Guid("c3d5e9f4-6a8b-4c0d-9e2f-4a7b0c1d3e5f");
        private const string PrefixSuffixSchemaName = "TurboNumberPrefixSuffix";
        private const string PrefixFieldName = "Prefix";
        private const string SuffixFieldName = "Suffix";

        private static Schema GetOrCreateKeypadSortSchema()
        {
            var schema = Schema.Lookup(KeypadSortSchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(KeypadSortSchemaGuid);
            builder.SetSchemaName(KeypadSortSchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(KeypadSortFieldName, typeof(bool));
            return builder.Finish();
        }

        private static Schema GetOrCreatePrefixSuffixSchema()
        {
            var schema = Schema.Lookup(PrefixSuffixSchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(PrefixSuffixSchemaGuid);
            builder.SetSchemaName(PrefixSuffixSchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(PrefixFieldName, typeof(string));
            builder.AddSimpleField(SuffixFieldName, typeof(string));
            return builder.Finish();
        }

        /// <summary>
        /// The persisted keypad room-sort toggle, or <c>null</c> when the project has never
        /// stored one — letting the caller apply a first-open default (sort on) while a user
        /// who deliberately turned it off (stored <c>false</c>) keeps it off.
        /// </summary>
        public static bool? LoadKeypadRoomSorted(Document doc)
        {
            var schema = Schema.Lookup(KeypadSortSchemaGuid);
            if (schema == null) return null;

            var storage = DataStorageHelper.FindDataStorage(doc, schema);
            if (storage == null) return null;

            var entity = storage.GetEntity(schema);
            if (!entity.IsValid()) return null;

            return entity.Get<bool>(KeypadSortFieldName);
        }

        public static void SaveKeypadRoomSorted(Document doc, bool isSorted)
        {
            var schema = GetOrCreateKeypadSortSchema();

            using (var tx = new Transaction(doc, "TurboNumber - Save Keypad Sort State"))
            {
                tx.Start();

                var storage = DataStorageHelper.FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
                var entity = new Entity(schema);
                entity.Set(KeypadSortFieldName, isSorted);
                storage.SetEntity(entity);

                tx.Commit();
            }
        }

        public static (string prefix, string suffix) LoadPrefixSuffix(Document doc)
        {
            var schema = Schema.Lookup(PrefixSuffixSchemaGuid);
            if (schema == null) return (null, null);

            var storage = DataStorageHelper.FindDataStorage(doc, schema);
            if (storage == null) return (null, null);

            var entity = storage.GetEntity(schema);
            if (!entity.IsValid()) return (null, null);

            return (entity.Get<string>(PrefixFieldName), entity.Get<string>(SuffixFieldName));
        }

        public static void SavePrefixSuffix(Document doc, string prefix, string suffix)
        {
            var schema = GetOrCreatePrefixSuffixSchema();

            using (var tx = new Transaction(doc, "TurboNumber - Save Prefix/Suffix"))
            {
                tx.Start();

                var storage = DataStorageHelper.FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
                var entity = new Entity(schema);
                entity.Set(PrefixFieldName, prefix ?? "");
                entity.Set(SuffixFieldName, suffix ?? "");
                storage.SetEntity(entity);

                tx.Commit();
            }
        }
    }
}
