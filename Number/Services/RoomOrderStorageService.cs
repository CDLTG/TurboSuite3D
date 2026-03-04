#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace TurboSuite.Number.Services
{
    public static class RoomOrderStorageService
    {
        private static readonly Guid SchemaGuid = new Guid("a1f3b7c2-4d6e-4a8b-9c0d-2e5f7a8b1c3d");
        private const string SchemaName = "TurboNumberRoomOrder";
        private const string FieldName = "RoomOrder";

        private static readonly Guid SidebarSchemaGuid = new Guid("b2e4c8d3-5f7a-4b9c-8d1e-3f6a9b0c2d4e");
        private const string SidebarSchemaName = "TurboNumberSidebarState";
        private const string SidebarFieldName = "IsSidebarVisible";

        private static readonly Guid PrefixSuffixSchemaGuid = new Guid("c3d5e9f4-6a8b-4c0d-9e2f-4a7b0c1d3e5f");
        private const string PrefixSuffixSchemaName = "TurboNumberPrefixSuffix";
        private const string PrefixFieldName = "Prefix";
        private const string SuffixFieldName = "Suffix";

        private static Schema GetOrCreateSchema()
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddArrayField(FieldName, typeof(string));
            return builder.Finish();
        }

        private static Schema GetOrCreateSidebarSchema()
        {
            var schema = Schema.Lookup(SidebarSchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(SidebarSchemaGuid);
            builder.SetSchemaName(SidebarSchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(SidebarFieldName, typeof(bool));
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

        private static DataStorage FindDataStorage(Document doc, Schema schema)
        {
            using (var collector = new FilteredElementCollector(doc))
            {
                return collector
                    .OfClass(typeof(DataStorage))
                    .Cast<DataStorage>()
                    .FirstOrDefault(ds => ds.GetEntity(schema).IsValid());
            }
        }

        public static List<string> Load(Document doc)
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return new List<string>();

            var storage = FindDataStorage(doc, schema);
            if (storage == null) return new List<string>();

            var entity = storage.GetEntity(schema);
            if (!entity.IsValid()) return new List<string>();

            return entity.Get<IList<string>>(FieldName)?.ToList() ?? new List<string>();
        }

        public static bool LoadSidebarVisible(Document doc)
        {
            var schema = Schema.Lookup(SidebarSchemaGuid);
            if (schema == null) return false;

            var storage = FindDataStorage(doc, schema);
            if (storage == null) return false;

            var entity = storage.GetEntity(schema);
            if (!entity.IsValid()) return false;

            return entity.Get<bool>(SidebarFieldName);
        }

        public static void Save(Document doc, List<string> roomOrder)
        {
            var schema = GetOrCreateSchema();

            using (var tx = new Transaction(doc, "TurboNumber - Save Room Order"))
            {
                tx.Start();

                var storage = FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
                var entity = new Entity(schema);
                entity.Set(FieldName, (IList<string>)roomOrder);
                storage.SetEntity(entity);

                tx.Commit();
            }
        }

        public static void SaveSidebarVisible(Document doc, bool isVisible)
        {
            var schema = GetOrCreateSidebarSchema();

            using (var tx = new Transaction(doc, "TurboNumber - Save Sidebar State"))
            {
                tx.Start();

                var storage = FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
                var entity = new Entity(schema);
                entity.Set(SidebarFieldName, isVisible);
                storage.SetEntity(entity);

                tx.Commit();
            }
        }

        public static (string prefix, string suffix) LoadPrefixSuffix(Document doc)
        {
            var schema = Schema.Lookup(PrefixSuffixSchemaGuid);
            if (schema == null) return (null, null);

            var storage = FindDataStorage(doc, schema);
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

                var storage = FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
                var entity = new Entity(schema);
                entity.Set(PrefixFieldName, prefix ?? "");
                entity.Set(SuffixFieldName, suffix ?? "");
                storage.SetEntity(entity);

                tx.Commit();
            }
        }
    }
}
