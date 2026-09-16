#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace TurboSuite.Shared.Services
{
    /// <summary>
    /// Project-wide room-order primitive: an ordered, per-room list of "Name|ClickOrder"
    /// entries persisted in ExtensibleStorage. Promoted out of TurboNumber into
    /// <c>TurboSuite.Shared.Services</c> so every module can consume one shared order
    /// (Phase A: TurboNumber writes it; Phase B wires TurboZones/TurboDocs onto it).
    ///
    /// The <see cref="SchemaGuid"/>, schema name, field name, and the
    /// <c>"Name|ClickOrder"</c> encoding are unchanged from the original
    /// TurboNumber-local store, so orders saved before the split load verbatim (no new
    /// GUID, no migration — the schema is byte-identical).
    /// </summary>
    public static class RoomOrderStorageService
    {
        private static readonly Guid SchemaGuid = new Guid("a1f3b7c2-4d6e-4a8b-9c0d-2e5f7a8b1c3d");
        private const string SchemaName = "TurboNumberRoomOrder";
        private const string FieldName = "RoomOrder";

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

        public static List<(string Name, int ClickOrder)> Load(Document doc)
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return new List<(string, int)>();

            var storage = DataStorageHelper.FindDataStorage(doc, schema);
            if (storage == null) return new List<(string, int)>();

            var entity = storage.GetEntity(schema);
            if (!entity.IsValid()) return new List<(string, int)>();

            var raw = entity.Get<IList<string>>(FieldName)?.ToList() ?? new List<string>();
            return raw.Select(ParseEntry).ToList();
        }

        private static (string Name, int ClickOrder) ParseEntry(string entry)
        {
            int sep = entry.LastIndexOf('|');
            if (sep >= 0 && int.TryParse(entry.Substring(sep + 1), out int order))
                return (entry.Substring(0, sep), order);
            return (entry, 0);
        }

        public static void Save(Document doc, List<(string Name, int ClickOrder)> roomOrder)
        {
            var schema = GetOrCreateSchema();

            var encoded = roomOrder
                .Select(r => r.ClickOrder > 0 ? $"{r.Name}|{r.ClickOrder}" : r.Name)
                .ToList();

            using (var tx = new Transaction(doc, "TurboSuite - Save Room Order"))
            {
                tx.Start();

                var storage = DataStorageHelper.FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
                var entity = new Entity(schema);
                entity.Set(FieldName, (IList<string>)encoded);
                storage.SetEntity(entity);

                tx.Commit();
            }
        }
    }
}
