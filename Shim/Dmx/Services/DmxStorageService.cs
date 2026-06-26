#nullable disable
using System;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using TurboSuite.Dmx.Persistence;
using TurboSuite.Shared.Services;

namespace TurboSuite.Dmx.Services
{
    /// <summary>
    /// The TurboDMX module's own document-side ExtensibleStorage — the single home for the design-intent
    /// overlays (module settings, declared loops, physical clusters, control-system tags, and the later
    /// solve snapshot). Doc-singleton, stored on a <see cref="DataStorage"/> element like the other
    /// settings services (NOT the shared TurboSuite Settings dialog — TurboDMX-Design §1.5).
    ///
    /// SHAPE (TurboDMX-BuildPlan Phase 0): the whole <see cref="DmxModuleState"/> bundle is serialized to
    /// one JSON document in the <c>StateJson</c> field, with a parallel <c>PayloadVersion</c> int for
    /// at-a-glance migration gating. Backing the nested overlays with JSON (rather than native ES
    /// array/map fields) keeps the ES field set fixed so Phases 1–3 grow the payload via PayloadVersion —
    /// no new schema GUID per shape change. The one exception that does NOT live here is the
    /// <c>Control Zone</c> instance parameter (native, on the tape) — this schema only references zones by
    /// their string value.
    ///
    /// Schema GUID is versioned — change it ONLY for a true ES field add/remove (see CLAUDE.md
    /// "ExtensibleStorage Schema Changes"); ordinary payload-shape growth bumps PayloadVersion instead.
    /// </summary>
    public static class DmxStorageService
    {
        private static readonly Guid SchemaGuid = new("e5f6a7b8-c9d0-1234-efab-456789012abc");
        private const string SchemaName = "TurboSuiteDmxModule";
        private const string StateJsonField = "StateJson";
        private const string PayloadVersionField = "PayloadVersion";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            // Compact on disk; tolerant on read.
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

        /// <summary>Reads the module state from the document. Returns a fresh default
        /// <see cref="DmxModuleState"/> when nothing is stored yet (or the payload can't be parsed).</summary>
        public static DmxModuleState Load(Document doc)
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return new DmxModuleState();

            var storage = DataStorageHelper.FindDataStorage(doc, schema);
            if (storage == null) return new DmxModuleState();

            var entity = storage.GetEntity(schema);
            if (!entity.IsValid()) return new DmxModuleState();

            if (schema.GetField(StateJsonField) == null) return new DmxModuleState();
            string json = entity.Get<string>(StateJsonField);
            if (string.IsNullOrWhiteSpace(json)) return new DmxModuleState();

            try
            {
                return JsonSerializer.Deserialize<DmxModuleState>(json, JsonOptions) ?? new DmxModuleState();
            }
            catch (JsonException)
            {
                // Corrupt/forward-incompatible payload — start clean rather than crash the window.
                return new DmxModuleState();
            }
        }

        /// <summary>Writes the module state to the document inside its own transaction.</summary>
        public static void Save(Document doc, DmxModuleState state)
        {
            if (state == null) state = new DmxModuleState();
            var schema = GetOrCreateSchema();
            string json = JsonSerializer.Serialize(state, JsonOptions);

            using var tx = new Transaction(doc, "TurboDMX - Save Module State");
            tx.Start();

            var storage = DataStorageHelper.FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
            var entity = new Entity(schema);
            entity.Set(StateJsonField, json);
            entity.Set(PayloadVersionField, state.PayloadVersion);
            storage.SetEntity(entity);

            tx.Commit();
        }
    }
}
