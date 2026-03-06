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
            WallSconceFamilies = ToHashSet(entity.Get<IList<string>>(WallSconceField)),
            ReceptacleFamilies = ToHashSet(entity.Get<IList<string>>(ReceptacleField)),
            ElectricalVerticalFamilies = ToHashSet(entity.Get<IList<string>>(ElectricalVerticalField)),
            VerticalFamilies = ToHashSet(entity.Get<IList<string>>(VerticalField))
        };
    }

    public static void Save(Document doc, FamilyNameSettings settings)
    {
        var schema = GetOrCreateSchema();

        using var tx = new Transaction(doc, "TurboSuite - Save Family Name Settings");
        tx.Start();

        var storage = DataStorageHelper.FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
        var entity = new Entity(schema);
        entity.Set(WallSconceField, (IList<string>)settings.WallSconceFamilies.ToList());
        entity.Set(ReceptacleField, (IList<string>)settings.ReceptacleFamilies.ToList());
        entity.Set(ElectricalVerticalField, (IList<string>)settings.ElectricalVerticalFamilies.ToList());
        entity.Set(VerticalField, (IList<string>)settings.VerticalFamilies.ToList());
        storage.SetEntity(entity);

        tx.Commit();
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
