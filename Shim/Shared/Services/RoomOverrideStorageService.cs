#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace TurboSuite.Shared.Services
{
    /// <summary>
    /// Per-circuit room-name overrides persisted in a single document-level
    /// <see cref="DataStorage"/>, keyed by circuit <c>UniqueId</c> → override text. The
    /// schema identity (GUID + name) is injected so the <b>same</b> read/write/merge logic
    /// backs two independent stores that must never clobber each other:
    ///   • lighting (<see cref="RoomOverrideStorageService"/>) and
    ///   • shades (<see cref="ShadeRoomOverrideStorageService"/>).
    ///
    /// Why two stores: a Load-Names "Apply" does a <see cref="Write"/> (full overwrite) built
    /// from <i>its own</i> snapshot of circuits, pruning any key it didn't enumerate. Lighting
    /// and shade circuits are collected by separate tabs with separate snapshots, so a single
    /// shared store would have each tab's Apply prune the other's overrides. Separate GUIDs keep
    /// each full-overwrite scoped to its own subsystem's circuits.
    ///
    /// This deliberately never stores the <i>base</i> room name — that is always recomputed live
    /// from fixture geometry (owned Spaces / region fallback). Only the user's explicit override
    /// lives here, so clearing it always falls back to whatever the geometry currently resolves to.
    /// </summary>
    internal sealed class RoomOverrideStore
    {
        private const string CircuitKeysField = "CircuitUniqueIds";
        private const string OverrideValuesField = "RoomOverrides";

        private readonly Guid _schemaGuid;
        private readonly string _schemaName;

        public RoomOverrideStore(Guid schemaGuid, string schemaName)
        {
            _schemaGuid = schemaGuid;
            _schemaName = schemaName;
        }

        private Schema GetOrCreateSchema()
        {
            var schema = Schema.Lookup(_schemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(_schemaGuid);
            builder.SetSchemaName(_schemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddArrayField(CircuitKeysField, typeof(string));
            builder.AddArrayField(OverrideValuesField, typeof(string));
            return builder.Finish();
        }

        public Dictionary<string, string> Load(Document doc)
        {
            var result = new Dictionary<string, string>();

            var schema = Schema.Lookup(_schemaGuid);
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

        public void Write(Document doc, IDictionary<string, string> overrides)
        {
            bool hasAny = overrides != null && overrides.Count > 0;

            var existingSchema = Schema.Lookup(_schemaGuid);
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

        public void Upsert(Document doc, IDictionary<string, string> changes)
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

    /// <summary>
    /// Lighting per-circuit room overrides. Shared between TurboZones (Load Names grid) and
    /// TurboWire (wire-time dialog), which read/write the same store so an override set in one
    /// tool is visible in the other. See <see cref="RoomOverrideStore"/> for semantics.
    ///
    /// (Formerly instance-less; the schema GUID is unchanged so existing stored overrides carry
    /// over.)
    /// </summary>
    public static class RoomOverrideStorageService
    {
        private static readonly RoomOverrideStore Store = new RoomOverrideStore(
            new Guid("1e1e5492-69a2-4ea0-b911-0c0ce104a17e"), "TurboZonesRoomOverridesV1");

        /// <summary>Reads the persisted overrides as a <c>circuit UniqueId → override</c> map.
        /// Empty (never null) when nothing has been saved. No transaction required.</summary>
        public static Dictionary<string, string> Load(Document doc) => Store.Load(doc);

        /// <summary>Writes the full override map, replacing whatever was stored before — so cleared
        /// overrides and deleted circuits absent from <paramref name="overrides"/> are pruned. Use
        /// only when the caller enumerated <b>every</b> lighting circuit (TurboZones' apply);
        /// otherwise use <see cref="Upsert"/>. <b>Assumes an already-open transaction.</b></summary>
        public static void Write(Document doc, IDictionary<string, string> overrides)
            => Store.Write(doc, overrides);

        /// <summary>Merges a handful of <c>circuit UniqueId → override</c> changes into the stored
        /// map, preserving untouched overrides. A blank/null value clears that circuit's override.
        /// Use from callers that know only a subset of circuits (TurboWire's dialog). <b>Assumes an
        /// already-open transaction.</b></summary>
        public static void Upsert(Document doc, IDictionary<string, string> changes)
            => Store.Upsert(doc, changes);
    }

    /// <summary>
    /// Shade per-circuit room overrides — the shade twin of <see cref="RoomOverrideStorageService"/>,
    /// on its own schema GUID so the TurboZones Shade Names tab's full-overwrite Apply and the
    /// lighting Load Names tab's Apply never prune each other's overrides. TurboWire routes a shade
    /// circuit's override here (its shade-mode dialog), and the shade circuit collector reads it.
    /// </summary>
    public static class ShadeRoomOverrideStorageService
    {
        private static readonly RoomOverrideStore Store = new RoomOverrideStore(
            new Guid("36d2d485-86e4-47a4-a553-3a367f24343c"), "TurboShadeRoomOverridesV1");

        /// <summary><see cref="RoomOverrideStorageService.Load"/>, for the shade store.</summary>
        public static Dictionary<string, string> Load(Document doc) => Store.Load(doc);

        /// <summary><see cref="RoomOverrideStorageService.Write"/>, for the shade store.</summary>
        public static void Write(Document doc, IDictionary<string, string> overrides)
            => Store.Write(doc, overrides);

        /// <summary><see cref="RoomOverrideStorageService.Upsert"/>, for the shade store.</summary>
        public static void Upsert(Document doc, IDictionary<string, string> changes)
            => Store.Upsert(doc, changes);
    }
}
