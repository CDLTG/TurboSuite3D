using System;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using TurboSuite.Shared.Models;

namespace TurboSuite.Shared.Services;

/// <summary>
/// Reads/writes <see cref="CadRoomSourceSettings"/> (TurboName CAD layer/block configuration) to
/// ExtensibleStorage in the project document.
///
/// SHAPE: the whole settings object is serialized to ONE JSON string field (plus a version int), exactly
/// like <c>DmxStorageService</c> — no native ES array fields. Payload-shape changes bump
/// <see cref="PayloadVersion"/> instead of the schema GUID.
///
/// NOTE (persistence gotcha): the command that saves these settings (TurboName) must return
/// <c>Result.Succeeded</c> — Revit DISCARDS a command's committed changes when it returns Cancelled/Failed,
/// which silently rolls back the saved DataStorage. See <c>NameCommand</c>.
///
/// Schema GUID is versioned — change it ONLY for a true ES field add/remove (see CLAUDE.md
/// "ExtensibleStorage Schema Changes").
/// </summary>
public static class CadRoomSourceStorageService
{
    // Unique schema name (prior V3–V5 all reused "TurboSuiteCadRoomSource"; never reuse a name across GUIDs).
    private static readonly Guid SchemaGuid = new("f1e2d3c4-b5a6-4789-9abc-de0123456789");
    private const string SchemaName = "TurboSuiteCadRoomSourceV6";
    private const string StateJsonField = "StateJson";
    private const string PayloadVersionField = "PayloadVersion";
    private const int PayloadVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private static Schema GetOrCreateSchema()
    {
        var schema = Schema.Lookup(SchemaGuid);
        if (schema != null) return schema;

        var builder = new SchemaBuilder(SchemaGuid);
        builder.SetSchemaName(SchemaName);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField(StateJsonField, typeof(string));
        builder.AddSimpleField(PayloadVersionField, typeof(int));
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

        if (schema.GetField(StateJsonField) == null) return null;
        string json = entity.Get<string>(StateJsonField);
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<CadRoomSourceSettings>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // Corrupt/forward-incompatible payload — start clean rather than crash.
            return null;
        }
    }

    public static void Save(Document doc, CadRoomSourceSettings settings)
    {
        if (settings == null) settings = CadRoomSourceSettings.CreateDefaults();
        var schema = GetOrCreateSchema();
        string json = JsonSerializer.Serialize(settings, JsonOptions);

        using var tx = new Transaction(doc, "TurboSuite - Save CAD Room Source Settings");
        tx.Start();

        var storage = DataStorageHelper.FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
        var entity = new Entity(schema);
        entity.Set(StateJsonField, json);
        entity.Set(PayloadVersionField, PayloadVersion);
        storage.SetEntity(entity);

        tx.Commit();
    }
}
