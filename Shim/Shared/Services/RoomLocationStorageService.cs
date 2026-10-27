#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace TurboSuite.Shared.Services
{
    /// <summary>
    /// Project-wide room→location primitive: a sparse, per-room list of
    /// "Name|Location" entries persisted in ExtensibleStorage, holding only the
    /// designer's <b>explicit</b> location picks (Phase A of the located-keypads plan).
    ///
    /// Deliberately a NEW, parallel store — not an extension of
    /// <see cref="RoomOrderStorageService"/> — for two reasons:
    /// <list type="bullet">
    /// <item>bumping the order store's GUID would reset the room order TurboNumber curates
    /// and TurboDocs reads, forcing a migration purely to preserve it; and</item>
    /// <item>the two model different things — room order is a <i>dense ordering over every
    /// room</i>, location is a <i>sparse membership</i> where a non-entry (blank) is a valid
    /// "this room routes nowhere." Keeping them apart lets each store's "absent" carry one
    /// unambiguous meaning.</item>
    /// </list>
    /// Both key by room name and self-heal from the live room enumeration each session.
    /// Only explicit picks live here; unanimous auto-seeds are re-derived at runtime by
    /// <c>RoomLocationSeeder</c> and never persisted.
    ///
    /// <para>Each entry is <c>Name|Location</c>, optionally suffixed <c>|O</c> to mark an
    /// <b>acknowledged override</b> — a pick the designer deliberately set on a <i>unanimous</i>
    /// room to route it against its own circuits (the rare mis-detection escape hatch). The flag
    /// is what lets <c>RoomLocationSeeder.Resolve</c> tell a deliberate override (resurfaces, wins)
    /// from an inert stale pick left on a since-gone-unanimous room (stays dormant). The suffix
    /// rides the existing array-of-string field, so adding it needs <b>no new schema GUID</b>.</para>
    /// </summary>
    public static class RoomLocationStorageService
    {
        // Brand-new schema GUID — never shared with RoomOrderStorageService (per CLAUDE.md
        // "ExtensibleStorage Schema Changes": a new field set requires a new GUID).
        private static readonly Guid SchemaGuid = new Guid("f4c8d1a6-9b2e-4f73-a5d0-6c1e8b4a2d9f");
        private const string SchemaName = "TurboSuiteRoomLocation";
        private const string FieldName = "RoomLocation";

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

        /// <summary>The store's id, or null when this project has none yet — for a session lease.</summary>
        public static ElementId FindStorageId(Document doc) => DataStorageHelper.FindStorageId(doc, SchemaGuid);

        /// <summary>Loads the persisted explicit room→location picks, each with its acknowledged-override
        /// flag (<c>true</c> only for a deliberate override of a unanimous room). Only rooms the designer
        /// picked appear; absent rooms are blank (resolved via auto-seed at runtime).</summary>
        public static List<(string Name, int Location, bool IsOverride)> Load(Document doc)
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return new List<(string, int, bool)>();

            var storage = DataStorageHelper.FindDataStorage(doc, schema);
            if (storage == null) return new List<(string, int, bool)>();

            var entity = storage.GetEntity(schema);
            if (!entity.IsValid()) return new List<(string, int, bool)>();

            var raw = entity.Get<IList<string>>(FieldName)?.ToList() ?? new List<string>();
            return raw.Select(ParseEntry)
                      .Where(e => e.Location > 0)
                      .ToList();
        }

        private static (string Name, int Location, bool IsOverride) ParseEntry(string entry)
        {
            if (string.IsNullOrEmpty(entry)) return (entry, 0, false);

            // Trailing "|O" marks an acknowledged override; strip it before the Name|Location split.
            bool isOverride = false;
            string body = entry;
            if (body.EndsWith("|O", StringComparison.Ordinal))
            {
                isOverride = true;
                body = body.Substring(0, body.Length - 2);
            }

            int sep = body.LastIndexOf('|');
            if (sep >= 0 && int.TryParse(body.Substring(sep + 1), out int loc))
                return (body.Substring(0, sep), loc, isOverride);
            return (body, 0, isOverride);
        }

        /// <summary>Persists the explicit picks. Entries with a non-positive location are
        /// dropped, so clearing a pick (location → 0) removes it from the store. An entry flagged
        /// <c>IsOverride</c> is suffixed <c>|O</c> so it survives as a deliberate override.</summary>
        /// <returns>False when Revit rolled the save back (nothing persisted).</returns>
        public static bool Save(Document doc, IReadOnlyList<(string Name, int Location, bool IsOverride)> roomLocations)
        {
            var schema = GetOrCreateSchema();

            var encoded = (roomLocations ?? Array.Empty<(string, int, bool)>())
                .Where(r => r.Location > 0 && !string.IsNullOrEmpty(r.Name))
                .Select(r => r.IsOverride ? $"{r.Name}|{r.Location}|O" : $"{r.Name}|{r.Location}")
                .ToList();

            using (var tx = new Transaction(doc, "TurboSuite - Save Room Locations"))
            {
                tx.Start();

                var storage = DataStorageHelper.FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
                var entity = new Entity(schema);
                entity.Set(FieldName, (IList<string>)encoded);
                storage.SetEntity(entity);

                return CommitGuard.TryCommit(tx);
            }
        }
    }
}
