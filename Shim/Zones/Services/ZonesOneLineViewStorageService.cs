#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using TurboSuite.Shared.Services;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// ExtensibleStorage for the control one-line + wire-legend owned-view ids
    /// (<see cref="OneLineViewState"/>). A DEDICATED schema (its own GUID) — deliberately NOT the
    /// panel-settings schema (<see cref="ZonesPanelSettingsStorageService"/>), so adding/changing it never
    /// re-bumps that GUID and resets the user's saved Panel Breakdown settings (see CLAUDE.md
    /// "ExtensibleStorage Schema Changes"). View ids (longs) are stored as strings — matching the
    /// panel-settings service's size-as-string convention — which also sidesteps the 2024 int / 2025+ long
    /// ElementRef seam.
    /// </summary>
    public static class ZonesOneLineViewStorageService
    {
        private static readonly Guid SchemaGuid = new Guid("3f8c1d26-5a74-4e39-b2c8-6d9af0147b53");
        private const string SchemaName = "TurboZonesOneLineViewsV1";
        private const string PageKeysField = "PageIndices";
        private const string PageValuesField = "PageViewIds";
        private const string WireLegendViewIdField = "WireLegendViewId";

        private static Schema GetOrCreateSchema()
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddArrayField(PageKeysField, typeof(string));
            builder.AddArrayField(PageValuesField, typeof(string));
            builder.AddSimpleField(WireLegendViewIdField, typeof(string));
            return builder.Finish();
        }

        private static DataStorage FindDataStorage(Document doc, Schema schema)
            => DataStorageHelper.FindDataStorage(doc, schema);

        public static OneLineViewState Load(Document doc)
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return new OneLineViewState();

            var storage = FindDataStorage(doc, schema);
            if (storage == null) return new OneLineViewState();

            var entity = storage.GetEntity(schema);
            if (!entity.IsValid()) return new OneLineViewState();

            var state = new OneLineViewState();

            var keys = entity.Get<IList<string>>(PageKeysField);
            var values = entity.Get<IList<string>>(PageValuesField);
            if (keys != null && values != null)
            {
                for (int i = 0; i < Math.Min(keys.Count, values.Count); i++)
                {
                    if (int.TryParse(keys[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int page)
                        && long.TryParse(values[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out long viewId))
                        state.PageViewIds[page] = viewId;
                }
            }

            if (long.TryParse(entity.Get<string>(WireLegendViewIdField), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out long legendId))
                state.WireLegendViewId = legendId;

            return state;
        }

        public static void Save(Document doc, OneLineViewState state)
        {
            state ??= new OneLineViewState();
            var schema = GetOrCreateSchema();

            using (var tx = new Transaction(doc, "TurboZones - Save One-Line Views"))
            {
                tx.Start();

                var storage = FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
                var entity = new Entity(schema);
                entity.Set(PageKeysField,
                    (IList<string>)state.PageViewIds.Keys.Select(k => k.ToString(CultureInfo.InvariantCulture)).ToList());
                entity.Set(PageValuesField,
                    (IList<string>)state.PageViewIds.Values.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToList());
                entity.Set(WireLegendViewIdField, state.WireLegendViewId.ToString(CultureInfo.InvariantCulture));
                storage.SetEntity(entity);

                tx.Commit();
            }
        }
    }
}
