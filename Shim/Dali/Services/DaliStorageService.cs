#nullable disable
using System;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using TurboSuite.Dali.Persistence;
using TurboSuite.Shared.Services;

namespace TurboSuite.Dali.Services
{
    /// <summary>
    /// The TurboDALI module's own document-side ExtensibleStorage — the home for the designer's declared
    /// DALI loops. Doc-singleton, stored on a <see cref="DataStorage"/> element, exactly like
    /// <c>DmxStorageService</c> (NOT the shared TurboSuite Settings dialog).
    ///
    /// SHAPE: the whole <see cref="DaliModuleState"/> bundle is serialized to one JSON document in the
    /// <c>StateJson</c> field, with a parallel <c>PayloadVersion</c> int for at-a-glance migration gating.
    /// Backing the loops with JSON (rather than native ES array fields) keeps the ES field set fixed, so
    /// later phases grow the payload via <c>PayloadVersion</c> — no new schema GUID per shape change. The
    /// <c>Control Zone</c> parameter is native (on the fixture); this schema only references zones by value.
    ///
    /// This is a BRAND-NEW schema (its own GUID), not a change to an existing one — so no stale-cache
    /// recovery drill applies (see CLAUDE.md "ExtensibleStorage Schema Changes"): there is nothing to
    /// migrate, DALI storage simply did not exist before. Change this GUID only for a true ES field
    /// add/remove; ordinary payload-shape growth bumps PayloadVersion instead.
    /// </summary>
    public static class DaliStorageService
    {
        private static readonly Guid SchemaGuid = new("26ac35a5-dcb9-489a-aec5-00e7b5ff0412");
        private const string SchemaName = "TurboSuiteDaliModule";
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
        /// <see cref="DaliModuleState"/> when nothing is stored yet (or the payload can't be parsed).</summary>
        public static DaliModuleState Load(Document doc)
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return new DaliModuleState();

            var storage = DataStorageHelper.FindDataStorage(doc, schema);
            if (storage == null) return new DaliModuleState();

            var entity = storage.GetEntity(schema);
            if (!entity.IsValid()) return new DaliModuleState();

            if (schema.GetField(StateJsonField) == null) return new DaliModuleState();
            string json = entity.Get<string>(StateJsonField);
            if (string.IsNullOrWhiteSpace(json)) return new DaliModuleState();

            try
            {
                var state = JsonSerializer.Deserialize<DaliModuleState>(json, JsonOptions) ?? new DaliModuleState();
                // A pre-v4 lock baseline anchors on circuit keys, a grain the reconciler can no longer pin —
                // drop it (loops survive, the job reverts to Unlocked). See DaliPayload.
                DaliPayload.DiscardStaleSnapshot(state);
                return state;
            }
            catch (JsonException)
            {
                // Corrupt/forward-incompatible payload — start clean rather than crash.
                return new DaliModuleState();
            }
        }

        /// <summary>Writes the module state to the document inside its own transaction.</summary>
        public static void Save(Document doc, DaliModuleState state)
        {
            if (state == null) state = new DaliModuleState();
            var schema = GetOrCreateSchema();
            string json = JsonSerializer.Serialize(state, JsonOptions);

            using var tx = new Transaction(doc, "TurboDALI - Save Module State");
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
