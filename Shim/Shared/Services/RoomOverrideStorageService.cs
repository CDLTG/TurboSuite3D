#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace TurboSuite.Shared.Services
{
    /// <summary>
    /// Persists per-circuit room-name overrides in a single document-level
    /// <see cref="DataStorage"/>, keyed by circuit <c>UniqueId</c> → override text.
    /// Shared between TurboZones (which surfaces and edits the override in its grid)
    /// and TurboWire (which lets the user set it at wire-time). Both read and write
    /// the <b>same</b> store under the same schema GUID, so an override set in one
    /// tool is visible in the other.
    ///
    /// This deliberately never stores the <i>base</i> room name — that is always
    /// recomputed live from fixture geometry (linked Rooms / region fallback). Only
    /// the user's explicit override lives here, so clearing it always falls back to
    /// whatever the geometry currently resolves to.
    ///
    /// (Formerly <c>ZonesRoomOverrideStorageService</c>; promoted to Shared. The
    /// schema GUID is unchanged so existing stored overrides carry over.)
    /// </summary>
    public static class RoomOverrideStorageService
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
        /// are pruned. Use this only when the caller has enumerated <b>every</b>
        /// circuit (TurboZones' apply); otherwise use <see cref="Upsert"/> so other
        /// circuits' overrides aren't clobbered. <b>Assumes an already-open
        /// transaction</b> (it composes into the caller's apply transaction).
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

        /// <summary>
        /// Merges a handful of <c>circuit UniqueId → override</c> changes into the
        /// stored map, preserving every override the caller didn't touch. A blank or
        /// null value clears (removes) that circuit's override. Use this from callers
        /// that only know about a subset of circuits (TurboWire's dialog) so they
        /// don't wipe overrides other tools set. <b>Assumes an already-open
        /// transaction.</b>
        /// </summary>
        public static void Upsert(Document doc, IDictionary<string, string> changes)
        {
            if (changes == null || changes.Count == 0) return;

            var map = Load(doc);
            foreach (var kvp in changes)
            {
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                if (string.IsNullOrWhiteSpace(kvp.Value))
                    map.Remove(kvp.Key);
                else
                    map[kvp.Key] = kvp.Value;
            }

            Write(doc, map);
        }
    }
}
