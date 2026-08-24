using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace TurboSuite.Shared.Services
{
    /// <summary>
    /// Persists the per-project TurboSchedule workbook path in ExtensibleStorage (one string field).
    /// Its own schema GUID — bump it on any field add/remove (see CLAUDE.md "ExtensibleStorage Schema
    /// Changes"). Mirrors <see cref="GeneralSettingsStorageService"/>'s Shape-A load/save.
    /// </summary>
    public static class WorkbookPathStorageService
    {
        private static readonly Guid SchemaGuid = new("a1b2c3d4-e5f6-4711-8a2b-9c0d1e2f3a4b");
        private const string SchemaName = "TurboSuiteScheduleWorkbookPath";
        private const string PathField = "WorkbookPath";

        private static Schema GetOrCreateSchema()
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(PathField, typeof(string));
            return builder.Finish();
        }

        /// <summary>The stored workbook path, or "" if none has been set for this project.</summary>
        public static string Load(Document doc)
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return "";

            var storage = DataStorageHelper.FindDataStorage(doc, schema);
            if (storage == null) return "";

            var entity = storage.GetEntity(schema);
            if (!entity.IsValid() || schema.GetField(PathField) == null) return "";
            return entity.Get<string>(PathField) ?? "";
        }

        public static void Save(Document doc, string path)
        {
            var schema = GetOrCreateSchema();

            using var tx = new Transaction(doc, "TurboSchedule - Save workbook path");
            tx.Start();

            var storage = DataStorageHelper.FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
            var entity = new Entity(schema);
            entity.Set(PathField, path ?? "");
            storage.SetEntity(entity);

            tx.Commit();
        }
    }
}
