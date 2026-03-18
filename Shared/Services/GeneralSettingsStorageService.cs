using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using TurboSuite.Shared.Models;

namespace TurboSuite.Shared.Services;

public static class GeneralSettingsStorageService
{
    // v1 schema — original single-field schema
    private static readonly Guid SchemaGuidV1 = new("c3d4e5f6-a7b8-9012-cdef-234567890abc");

    // v2 schema — adds AutoSplitFixtures
    private static readonly Guid SchemaGuidV2 = new("c3d4e5f6-a7b8-9012-cdef-234567890abd");
    private const string SchemaName = "TurboSuiteGeneralSettingsV2";
    private const string ShowCommentsDialogField = "ShowCircuitCommentsDialog";
    private const string AutoSplitFixturesField = "AutoSplitFixtures";

    private static Schema GetOrCreateSchemaV2()
    {
        var schema = Schema.Lookup(SchemaGuidV2);
        if (schema != null) return schema;

        var builder = new SchemaBuilder(SchemaGuidV2);
        builder.SetSchemaName(SchemaName);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField(ShowCommentsDialogField, typeof(bool));
        builder.AddSimpleField(AutoSplitFixturesField, typeof(bool));
        return builder.Finish();
    }

    public static GeneralSettings? Load(Document doc)
    {
        // Try v2 first
        var schemaV2 = Schema.Lookup(SchemaGuidV2);
        if (schemaV2 != null)
        {
            var storage = DataStorageHelper.FindDataStorage(doc, schemaV2);
            if (storage != null)
            {
                var entity = storage.GetEntity(schemaV2);
                if (entity.IsValid())
                {
                    return new GeneralSettings
                    {
                        ShowCircuitCommentsDialog = entity.Get<bool>(ShowCommentsDialogField),
                        AutoSplitFixtures = entity.Get<bool>(AutoSplitFixturesField)
                    };
                }
            }
        }

        // Fall back to v1
        var schemaV1 = Schema.Lookup(SchemaGuidV1);
        if (schemaV1 == null) return null;

        var storageV1 = DataStorageHelper.FindDataStorage(doc, schemaV1);
        if (storageV1 == null) return null;

        var entityV1 = storageV1.GetEntity(schemaV1);
        if (!entityV1.IsValid()) return null;

        return new GeneralSettings
        {
            ShowCircuitCommentsDialog = entityV1.Get<bool>(ShowCommentsDialogField),
            AutoSplitFixtures = true // default for documents saved with v1
        };
    }

    public static void Save(Document doc, GeneralSettings settings)
    {
        var schema = GetOrCreateSchemaV2();

        using var tx = new Transaction(doc, "TurboSuite - Save General Settings");
        tx.Start();

        // Clean up v1 storage if it exists
        var schemaV1 = Schema.Lookup(SchemaGuidV1);
        if (schemaV1 != null)
        {
            var oldStorage = DataStorageHelper.FindDataStorage(doc, schemaV1);
            if (oldStorage != null)
                doc.Delete(oldStorage.Id);
        }

        var storage = DataStorageHelper.FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
        var entity = new Entity(schema);
        entity.Set(ShowCommentsDialogField, settings.ShowCircuitCommentsDialog);
        entity.Set(AutoSplitFixturesField, settings.AutoSplitFixtures);
        storage.SetEntity(entity);

        tx.Commit();
    }
}
