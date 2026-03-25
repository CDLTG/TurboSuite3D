using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using TurboSuite.Shared.Models;

namespace TurboSuite.Shared.Services;

public static class GeneralSettingsStorageService
{
    private static readonly Guid SchemaGuid = new("c3d4e5f6-a7b8-9012-cdef-234567890abd");
    private const string SchemaName = "TurboSuiteGeneralSettings";
    private const string ShowCommentsDialogField = "ShowCircuitCommentsDialog";
    private const string AutoSplitFixturesField = "AutoSplitFixtures";

    private static Schema GetOrCreateSchema()
    {
        var schema = Schema.Lookup(SchemaGuid);
        if (schema != null) return schema;

        var builder = new SchemaBuilder(SchemaGuid);
        builder.SetSchemaName(SchemaName);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField(ShowCommentsDialogField, typeof(bool));
        builder.AddSimpleField(AutoSplitFixturesField, typeof(bool));
        return builder.Finish();
    }

    public static GeneralSettings? Load(Document doc)
    {
        var schema = Schema.Lookup(SchemaGuid);
        if (schema == null) return null;

        var storage = DataStorageHelper.FindDataStorage(doc, schema);
        if (storage == null) return null;

        var entity = storage.GetEntity(schema);
        if (!entity.IsValid()) return null;

        return new GeneralSettings
        {
            ShowCircuitCommentsDialog = schema.GetField(ShowCommentsDialogField) != null && entity.Get<bool>(ShowCommentsDialogField),
            AutoSplitFixtures = schema.GetField(AutoSplitFixturesField) != null && entity.Get<bool>(AutoSplitFixturesField)
        };
    }

    public static void Save(Document doc, GeneralSettings settings)
    {
        var schema = GetOrCreateSchema();

        using var tx = new Transaction(doc, "TurboSuite - Save General Settings");
        tx.Start();

        var storage = DataStorageHelper.FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
        var entity = new Entity(schema);
        if (schema.GetField(ShowCommentsDialogField) != null)
            entity.Set(ShowCommentsDialogField, settings.ShowCircuitCommentsDialog);
        if (schema.GetField(AutoSplitFixturesField) != null)
            entity.Set(AutoSplitFixturesField, settings.AutoSplitFixtures);
        storage.SetEntity(entity);

        tx.Commit();
    }
}
