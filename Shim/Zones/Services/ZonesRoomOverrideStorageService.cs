#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using TurboSuite.Shared.Services;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Persists per-circuit room-name overrides for TurboZones in a single
    /// document-level <see cref="DataStorage"/>, keyed by circuit
    /// <c>UniqueId</c> → override text.
    ///
    /// This replaces the old (corrupting) behaviour of writing the override into
    /// the region's <c>Comments</c> — which doubled as the room-name read source,
    /// so a per-circuit override bled to every circuit in the region. Storing the
    /// override here, separate from any room-name source, keeps it scoped to the
    /// one circuit and lets it survive a window reopen.
    /// </summary>
    public static class ZonesRoomOverrideStorageService
    {
        private static readonly Guid SchemaGuid = new Guid("1e1e5492-69a2-4ea0-b911-0c0ce104a17e");
        private const string SchemaName = "TurboZonesRoomOverridesV1";
        private const string CircuitKeysField = "CircuitUniqueIds";
        private const string OverrideValuesField = "RoomOverrides";

        private static Schema GetOrCreateSchema()
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddArrayField(CircuitKeysField, typeof(string));
            builder.AddArrayField(OverrideValuesField, typeof(string));
            return builder.Finish();
        }

        /// <summary>
        /// Reads the persisted overrides as a <c>circuit UniqueId → override</c> map.
        /// Returns an empty (never null) map when nothing has been saved. No
        /// transaction required.
        /// </summary>
        public static Dictionary<string, string> Load(Document doc)
        {
            var result = new Dictionary<string, string>();

            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return result;

            var storage = DataStorageHelper.FindDataStorage(doc, schema);
            if (storage == null) return result;

            var entity = storage.GetEntity(schema);
            if (!entity.IsValid()) return result;

            var keys = entity.Get<IList<string>>(CircuitKeysField);
            var values = entity.Get<IList<string>>(OverrideValuesField);
            if (keys != null && values != null)
            {
                for (int i = 0; i < Math.Min(keys.Count, values.Count); i++)
                {
                    if (!string.IsNullOrEmpty(keys[i]))
                        result[keys[i]] = values[i];
                }
            }

            return result;
        }

        /// <summary>
        /// Writes the full override map, replacing whatever was stored before — so
        /// cleared overrides and deleted circuits absent from <paramref name="overrides"/>
        /// are pruned. <b>Assumes an already-open transaction</b> (it composes into
        /// the caller's apply transaction; it does not open its own).
        /// </summary>
        public static void Write(Document doc, IDictionary<string, string> overrides)
        {
            bool hasAny = overrides != null && overrides.Count > 0;

            var existingSchema = Schema.Lookup(SchemaGuid);
            var existingStorage = existingSchema != null
                ? DataStorageHelper.FindDataStorage(doc, existingSchema)
                : null;

            // Nothing to persist and nothing previously persisted — don't create an
            // empty DataStorage just to hold an empty map.
            if (!hasAny && existingStorage == null) return;

            var schema = GetOrCreateSchema();
            var storage = existingStorage ?? DataStorage.Create(doc);

            var keys = hasAny ? overrides.Keys.ToList() : new List<string>();
            var values = hasAny ? overrides.Values.ToList() : new List<string>();

            var entity = new Entity(schema);
            entity.Set(CircuitKeysField, (IList<string>)keys);
            entity.Set(OverrideValuesField, (IList<string>)values);
            storage.SetEntity(entity);
        }
    }
}
