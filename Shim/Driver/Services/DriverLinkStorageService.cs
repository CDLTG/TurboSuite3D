#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using TurboSuite.Abstractions;
using TurboSuite.Driver.Models;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Services;
using TurboSuite.Shared.Storage;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Per-project fixture-type → driver-family links (TurboRPS → Driver Match). A doc-singleton store
    /// modelled on <see cref="RoomLocationStorageService"/>: one array-of-string field, each entry
    /// <c>"&lt;fixtureTypeUniqueId&gt;|&lt;driverFamilyUniqueId&gt;"</c>. UniqueIds, not ElementIds, so a
    /// link survives across sessions and worksharing.
    ///
    /// <para>A link whose fixture type or driver family no longer resolves is <b>broken</b>: the matcher
    /// ignores it (<see cref="LoadActive"/>) and the Driver Match list shows it flagged so the user can
    /// remove it. Broken entries are kept in the store until the user unlinks them.</para>
    /// </summary>
    public static class DriverLinkStorageService
    {
        // Brand-new schema GUID (per CLAUDE.md "ExtensibleStorage Schema Changes").
        private static readonly Guid SchemaGuid = new Guid("6bd83666-ecc0-42b0-a8d8-186b7b37e555");
        private const string SchemaName = "TurboSuiteDriverLinks";
        private const string FieldName = "DriverLinks";
        private const char Separator = '|';

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

        /// <summary>The store's id, or null when this project has none yet (a first save creates it,
        /// owned by its creator — nothing to borrow).</summary>
        public static ElementId FindStorageId(Document doc) => DataStorageHelper.FindStorageId(doc, SchemaGuid);

        /// <summary>The raw persisted (fixtureTypeKey, driverFamilyKey) pairs, in stored order.</summary>
        public static List<(string FixtureTypeKey, string DriverFamilyKey)> LoadRaw(Document doc)
        {
            var result = new List<(string, string)>();
            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return result;

            var storage = DataStorageHelper.FindDataStorage(doc, schema);
            if (storage == null) return result;

            var entity = storage.GetEntity(schema);
            if (!entity.IsValid()) return result;

            var raw = entity.Get<IList<string>>(FieldName) ?? new List<string>();
            foreach (var entry in raw)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                int sep = entry.IndexOf(Separator);
                if (sep <= 0 || sep >= entry.Length - 1) continue;
                result.Add((entry.Substring(0, sep), entry.Substring(sep + 1)));
            }
            return result;
        }

        /// <summary>Every stored link, resolved against the document (broken ones flagged).</summary>
        public static List<DriverLink> LoadAll(Document doc)
        {
            var links = new List<DriverLink>();
            foreach (var (fixtureKey, familyKey) in LoadRaw(doc))
            {
                var fixtureType = doc.GetElement(fixtureKey) as FamilySymbol;
                var family = doc.GetElement(familyKey) as Family;

                links.Add(new DriverLink
                {
                    FixtureTypeKey = fixtureKey,
                    DriverFamilyKey = familyKey,
                    FixtureTypeRef = fixtureType?.Id.ToRef() ?? ElementRef.None,
                    DriverFamilyRef = family?.Id.ToRef() ?? ElementRef.None,
                    FixtureDisplay = fixtureType != null
                        ? $"{fixtureType.FamilyName} : {fixtureType.Name}"
                        : "(deleted fixture type)",
                    FamilyDisplay = family != null ? family.Name : "(deleted driver family)",
                    IsBroken = fixtureType == null || family == null
                });
            }

            return links
                .OrderBy(l => l.FixtureDisplay, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>The matcher's view: non-broken links only.</summary>
        public static DriverLinks LoadActive(Document doc) => DriverLinks.From(LoadAll(doc));

        /// <summary>Persist the full link list in its own transaction (one undo step). One entry per
        /// fixture type — a later duplicate key wins.</summary>
        /// <returns>Saved, RolledBack (worksharing denial — report it), or Blocked (the guard already told the user).</returns>
        public static StorageSaveOutcome Save(Document doc, IEnumerable<(string FixtureTypeKey, string DriverFamilyKey)> entries)
        {
            var schema = GetOrCreateSchema();

            var byFixture = new Dictionary<string, string>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var (fixtureKey, familyKey) in entries ?? Enumerable.Empty<(string, string)>())
            {
                if (string.IsNullOrEmpty(fixtureKey) || string.IsNullOrEmpty(familyKey)) continue;
                if (!byFixture.ContainsKey(fixtureKey)) order.Add(fixtureKey);
                byFixture[fixtureKey] = familyKey;
            }
            var encoded = order.Select(k => $"{k}{Separator}{byFixture[k]}").ToList();

            return DataStorageHelper.SaveEntity(doc, schema, "TurboRPS driver links", "TurboRPS - Save Driver Links",
                entity => entity.Set(FieldName, (IList<string>)encoded));
        }
    }
}
