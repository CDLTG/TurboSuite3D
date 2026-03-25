using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using TurboSuite.Shared.Models;

namespace TurboSuite.Shared.Services;

public static class FamilyNameSettingsStorageService
{
    private static readonly Guid SchemaGuid = new("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
    private const string SchemaName = "TurboSuiteFamilyNameSettings";
    private const string WallSconceField = "WallSconceFamilies";
    private const string ReceptacleField = "ReceptacleFamilies";
    private const string ElectricalVerticalField = "ElectricalVerticalFamilies";
    private const string VerticalField = "VerticalFamilies";
    private const string SwitchField = "SwitchFamilies";

    private static Schema GetOrCreateSchema()
    {
        var schema = Schema.Lookup(SchemaGuid);
        if (schema != null) return schema;

        var builder = new SchemaBuilder(SchemaGuid);
        builder.SetSchemaName(SchemaName);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddArrayField(WallSconceField, typeof(string));
        builder.AddArrayField(ReceptacleField, typeof(string));
        builder.AddArrayField(ElectricalVerticalField, typeof(string));
        builder.AddArrayField(VerticalField, typeof(string));
        builder.AddArrayField(SwitchField, typeof(string));
        return builder.Finish();
    }

    public static FamilyNameSettings? Load(Document doc)
    {
        var schema = Schema.Lookup(SchemaGuid);
        if (schema == null) return null;

        var storage = DataStorageHelper.FindDataStorage(doc, schema);
        if (storage == null) return null;

        var entity = storage.GetEntity(schema);
        if (!entity.IsValid()) return null;

        return new FamilyNameSettings
        {
            WallSconceFamilies = GetArrayField(entity, schema, WallSconceField),
            ReceptacleFamilies = GetArrayField(entity, schema, ReceptacleField),
            ElectricalVerticalFamilies = GetArrayField(entity, schema, ElectricalVerticalField),
            VerticalFamilies = GetArrayField(entity, schema, VerticalField),
            SwitchFamilies = GetArrayField(entity, schema, SwitchField)
        };
    }

    public static void Save(Document doc, FamilyNameSettings settings)
    {
        var schema = GetOrCreateSchema();

        using var tx = new Transaction(doc, "TurboSuite - Save Family Name Settings");
        tx.Start();

        var storage = DataStorageHelper.FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
        var entity = new Entity(schema);
        SetArrayField(entity, schema, WallSconceField, settings.WallSconceFamilies);
        SetArrayField(entity, schema, ReceptacleField, settings.ReceptacleFamilies);
        SetArrayField(entity, schema, ElectricalVerticalField, settings.ElectricalVerticalFamilies);
        SetArrayField(entity, schema, VerticalField, settings.VerticalFamilies);
        SetArrayField(entity, schema, SwitchField, settings.SwitchFamilies);
        storage.SetEntity(entity);

        tx.Commit();
    }

    private static HashSet<string> GetArrayField(Entity entity, Schema schema, string fieldName)
    {
        if (schema.GetField(fieldName) == null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ToHashSet(entity.Get<IList<string>>(fieldName));
    }

    private static void SetArrayField(Entity entity, Schema schema, string fieldName, HashSet<string> values)
    {
        if (schema.GetField(fieldName) != null)
            entity.Set(fieldName, (IList<string>)values.ToList());
    }

    private static HashSet<string> ToHashSet(IList<string>? list)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (list != null)
            foreach (var item in list)
                if (!string.IsNullOrWhiteSpace(item))
                    set.Add(item);
        return set;
    }
}
