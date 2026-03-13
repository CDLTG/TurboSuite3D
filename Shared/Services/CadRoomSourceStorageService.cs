using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using TurboSuite.Shared.Models;

namespace TurboSuite.Shared.Services;

public static class CadRoomSourceStorageService
{
    private static readonly Guid SchemaGuid = new("b2c3d4e5-f6a7-8901-bcde-f12345678901");
    private const string SchemaName = "TurboSuiteCadRoomSource";
    private const string ModeField = "Mode";
    private const string BlockNameField = "BlockName";
    private const string RoomNameTagsField = "RoomNameTags";
    private const string CeilingHeightTagField = "CeilingHeightTag";
    private const string RoomNameLayerField = "RoomNameLayer";
    private const string CeilingHeightLayerField = "CeilingHeightLayer";

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
            Mode = entity.Get<string>(ModeField) ?? "Block",
            BlockName = entity.Get<string>(BlockNameField) ?? "",
            RoomNameTags = entity.Get<IList<string>>(RoomNameTagsField)?.ToList() ?? new List<string>(),
            CeilingHeightTag = entity.Get<string>(CeilingHeightTagField) ?? "",
            RoomNameLayer = entity.Get<string>(RoomNameLayerField) ?? "",
            CeilingHeightLayer = entity.Get<string>(CeilingHeightLayerField) ?? ""
        };
    }

    public static void Save(Document doc, CadRoomSourceSettings settings)
    {
        var schema = GetOrCreateSchema();

        using var tx = new Transaction(doc, "TurboSuite - Save CAD Room Source Settings");
        tx.Start();

        var storage = DataStorageHelper.FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
        var entity = new Entity(schema);
        entity.Set(ModeField, settings.Mode ?? "Block");
        entity.Set(BlockNameField, settings.BlockName ?? "");
        entity.Set(RoomNameTagsField, (IList<string>)(settings.RoomNameTags ?? new List<string>()));
        entity.Set(CeilingHeightTagField, settings.CeilingHeightTag ?? "");
        entity.Set(RoomNameLayerField, settings.RoomNameLayer ?? "");
        entity.Set(CeilingHeightLayerField, settings.CeilingHeightLayer ?? "");
        storage.SetEntity(entity);

        tx.Commit();
    }
}
