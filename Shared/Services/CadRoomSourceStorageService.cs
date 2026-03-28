using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using TurboSuite.Shared.Models;

namespace TurboSuite.Shared.Services;

public static class CadRoomSourceStorageService
{
    // V3: new GUID to add CeilingHeightBlockName/CeilingHeightBlockTag fields.
    // Old schemas are purged via TurboSpike, but Schema.Lookup caches persist in memory.
    private static readonly Guid SchemaGuid = new("b2c3d4e5-f6a7-8901-bcde-f12345678903");
    private const string SchemaName = "TurboSuiteCadRoomSource";
    private const string ModeField = "Mode";
    private const string BlockNameField = "BlockName";
    private const string RoomNameTagsField = "RoomNameTags";
    private const string CeilingHeightTagField = "CeilingHeightTag";
    private const string RoomNameLayerField = "RoomNameLayer";
    private const string CeilingHeightLayerField = "CeilingHeightLayer";
    private const string CeilingHeightBlockNameField = "CeilingHeightBlockName";
    private const string CeilingHeightBlockTagField = "CeilingHeightBlockTag";
    private const string WallLayerNamesField = "WallLayerNames";
    private const string DoorLayerNamesField = "DoorLayerNames";
    private const string WindowLayerNamesField = "WindowLayerNames";
    private const string RegionTypeNameField = "RegionTypeName";

    private static Schema GetOrCreateSchema()
    {
        var schema = Schema.Lookup(SchemaGuid);
        if (schema != null) return schema;

        var builder = new SchemaBuilder(SchemaGuid);
        builder.SetSchemaName(SchemaName);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField(ModeField, typeof(string));
        builder.AddSimpleField(BlockNameField, typeof(string));
        builder.AddArrayField(RoomNameTagsField, typeof(string));
        builder.AddSimpleField(CeilingHeightTagField, typeof(string));
        builder.AddSimpleField(RoomNameLayerField, typeof(string));
        builder.AddSimpleField(CeilingHeightLayerField, typeof(string));
        builder.AddSimpleField(CeilingHeightBlockNameField, typeof(string));
        builder.AddSimpleField(CeilingHeightBlockTagField, typeof(string));
        builder.AddArrayField(WallLayerNamesField, typeof(string));
        builder.AddArrayField(DoorLayerNamesField, typeof(string));
        builder.AddArrayField(WindowLayerNamesField, typeof(string));
        builder.AddSimpleField(RegionTypeNameField, typeof(string));
        return builder.Finish();
    }

    public static CadRoomSourceSettings? Load(Document doc)
    {
        var schema = Schema.Lookup(SchemaGuid);
        if (schema == null) return null;

        var storage = DataStorageHelper.FindDataStorage(doc, schema);
        if (storage == null) return null;

        var entity = storage.GetEntity(schema);
        if (!entity.IsValid()) return null;

        return new CadRoomSourceSettings
        {
            Mode = GetStringField(entity, schema, ModeField, "Block"),
            BlockName = GetStringField(entity, schema, BlockNameField, ""),
            RoomNameTags = schema.GetField(RoomNameTagsField) != null
                ? entity.Get<IList<string>>(RoomNameTagsField)?.ToList() ?? new List<string>()
                : new List<string>(),
            CeilingHeightTag = GetStringField(entity, schema, CeilingHeightTagField, ""),
            RoomNameLayer = GetStringField(entity, schema, RoomNameLayerField, ""),
            CeilingHeightLayer = GetStringField(entity, schema, CeilingHeightLayerField, ""),
            CeilingHeightBlockName = GetStringField(entity, schema, CeilingHeightBlockNameField, ""),
            CeilingHeightBlockTag = GetStringField(entity, schema, CeilingHeightBlockTagField, ""),
            WallLayerNames = schema.GetField(WallLayerNamesField) != null
                ? entity.Get<IList<string>>(WallLayerNamesField)?.ToList() ?? new List<string>()
                : new List<string>(),
            DoorLayerNames = schema.GetField(DoorLayerNamesField) != null
                ? entity.Get<IList<string>>(DoorLayerNamesField)?.ToList() ?? new List<string>()
                : new List<string>(),
            WindowLayerNames = schema.GetField(WindowLayerNamesField) != null
                ? entity.Get<IList<string>>(WindowLayerNamesField)?.ToList() ?? new List<string>()
                : new List<string>(),
            RegionTypeName = GetStringField(entity, schema, RegionTypeNameField, "Room Region")
        };
    }

    private static string GetStringField(Entity entity, Schema schema, string fieldName, string defaultValue)
    {
        if (schema.GetField(fieldName) == null) return defaultValue;
        return entity.Get<string>(fieldName) ?? defaultValue;
    }

    private static void SetStringField(Entity entity, Schema schema, string fieldName, string value)
    {
        if (schema.GetField(fieldName) != null)
            entity.Set(fieldName, value);
    }

    public static void Save(Document doc, CadRoomSourceSettings settings)
    {
        var schema = GetOrCreateSchema();

        using var tx = new Transaction(doc, "TurboSuite - Save CAD Room Source Settings");
        tx.Start();

        var storage = DataStorageHelper.FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
        var entity = new Entity(schema);
        SetStringField(entity, schema, ModeField, settings.Mode ?? "Block");
        SetStringField(entity, schema, BlockNameField, settings.BlockName ?? "");
        if (schema.GetField(RoomNameTagsField) != null)
            entity.Set(RoomNameTagsField, (IList<string>)(settings.RoomNameTags ?? new List<string>()));
        SetStringField(entity, schema, CeilingHeightTagField, settings.CeilingHeightTag ?? "");
        SetStringField(entity, schema, RoomNameLayerField, settings.RoomNameLayer ?? "");
        SetStringField(entity, schema, CeilingHeightLayerField, settings.CeilingHeightLayer ?? "");
        SetStringField(entity, schema, CeilingHeightBlockNameField, settings.CeilingHeightBlockName ?? "");
        SetStringField(entity, schema, CeilingHeightBlockTagField, settings.CeilingHeightBlockTag ?? "");
        if (schema.GetField(WallLayerNamesField) != null)
            entity.Set(WallLayerNamesField, (IList<string>)(settings.WallLayerNames ?? new List<string>()));
        if (schema.GetField(DoorLayerNamesField) != null)
            entity.Set(DoorLayerNamesField, (IList<string>)(settings.DoorLayerNames ?? new List<string>()));
        if (schema.GetField(WindowLayerNamesField) != null)
            entity.Set(WindowLayerNamesField, (IList<string>)(settings.WindowLayerNames ?? new List<string>()));
        SetStringField(entity, schema, RegionTypeNameField, settings.RegionTypeName ?? "Room Region");
        storage.SetEntity(entity);

        tx.Commit();
    }
}
