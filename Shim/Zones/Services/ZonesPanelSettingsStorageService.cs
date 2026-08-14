#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using TurboSuite.Shared.Services;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    public static class ZonesPanelSettingsStorageService
    {
        // V3 (new GUID): added AllowRelayZeroTenPackingField. Old V2 entities are cached in Revit's
        // memory and cannot be extended at runtime, so a new schema is required — see CLAUDE.md
        // "ExtensibleStorage Schema Changes" for the coordinated-rollout recovery procedure.
        private static readonly Guid SchemaGuid = new Guid("f4a1c2d3-5e6f-4a7b-8c9d-0e1f2a3b4c5d");
        private const string SchemaName = "TurboZonesPanelSettingsV3";
        private const string BrandField = "Brand";
        private const string UseDedicatedRelayModuleField = "UseDedicatedRelayModule";
        private const string AllowRelayZeroTenPackingField = "AllowRelayZeroTenPacking";
        private const string SpecialKeysField = "SpecialDeviceKeys";
        private const string SpecialValuesField = "SpecialDeviceValues";
        private const string PanelSizeKeysField = "PanelSizeKeys";
        private const string PanelSizeValuesField = "PanelSizeValues";

        private static Schema GetOrCreateSchema()
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(BrandField, typeof(string));
            builder.AddSimpleField(UseDedicatedRelayModuleField, typeof(string));
            builder.AddSimpleField(AllowRelayZeroTenPackingField, typeof(string));
            builder.AddArrayField(SpecialKeysField, typeof(string));
            builder.AddArrayField(SpecialValuesField, typeof(string));
            builder.AddArrayField(PanelSizeKeysField, typeof(string));
            builder.AddArrayField(PanelSizeValuesField, typeof(string));
            return builder.Finish();
        }

        private static DataStorage FindDataStorage(Document doc, Schema schema)
            => DataStorageHelper.FindDataStorage(doc, schema);

        public static PanelSettings Load(Document doc)
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return null;

            var storage = FindDataStorage(doc, schema);
            if (storage == null) return null;

            var entity = storage.GetEntity(schema);
            if (!entity.IsValid()) return null;

            return LoadFromEntity(entity, schema);
        }

        private static PanelSettings LoadFromEntity(Entity entity, Schema schema)
        {
            var settings = new PanelSettings
            {
                Brand = entity.Get<string>(BrandField),
                UseDedicatedRelayModule = string.Equals(entity.Get<string>(UseDedicatedRelayModuleField), "true", StringComparison.OrdinalIgnoreCase),
                AllowRelayZeroTenPacking = string.Equals(entity.Get<string>(AllowRelayZeroTenPackingField), "true", StringComparison.OrdinalIgnoreCase)
            };

            var keys = entity.Get<IList<string>>(SpecialKeysField);
            var values = entity.Get<IList<string>>(SpecialValuesField);
            if (keys != null && values != null)
            {
                for (int i = 0; i < Math.Min(keys.Count, values.Count); i++)
                    settings.SpecialDeviceSelections[keys[i]] = values[i];
            }

            var sizeKeys = entity.Get<IList<string>>(PanelSizeKeysField);
            var sizeValues = entity.Get<IList<string>>(PanelSizeValuesField);
            if (sizeKeys != null && sizeValues != null)
            {
                for (int i = 0; i < Math.Min(sizeKeys.Count, sizeValues.Count); i++)
                {
                    if (int.TryParse(sizeValues[i], out int size))
                        settings.PanelSizeOverrides[sizeKeys[i]] = size;
                }
            }

            return settings;
        }

        public static void Save(Document doc, PanelSettings settings)
        {
            var schema = GetOrCreateSchema();

            using (var tx = new Transaction(doc, "TurboZones - Save Panel Settings"))
            {
                tx.Start();

                var storage = FindDataStorage(doc, schema) ?? DataStorage.Create(doc);
                var entity = new Entity(schema);
                entity.Set(BrandField, settings.Brand ?? "Lutron");
                entity.Set(UseDedicatedRelayModuleField, settings.UseDedicatedRelayModule ? "true" : "false");
                entity.Set(AllowRelayZeroTenPackingField, settings.AllowRelayZeroTenPacking ? "true" : "false");
                entity.Set(SpecialKeysField, (IList<string>)settings.SpecialDeviceSelections.Keys.ToList());
                entity.Set(SpecialValuesField, (IList<string>)settings.SpecialDeviceSelections.Values.ToList());
                entity.Set(PanelSizeKeysField, (IList<string>)settings.PanelSizeOverrides.Keys.ToList());
                entity.Set(PanelSizeValuesField, (IList<string>)settings.PanelSizeOverrides.Values.Select(v => v.ToString()).ToList());
                storage.SetEntity(entity);

                tx.Commit();
            }
        }
    }
}
