#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace TurboSuite.Zones.Services
{
    public class PanelSettings
    {
        public string Brand { get; set; }
        public Dictionary<string, string> SpecialDeviceSelections { get; set; } = new Dictionary<string, string>();
    }

    public static class ZonesPanelSettingsStorageService
    {
        // V3 schema — brand + special device selections only (panel size now from Revit)
        private static readonly Guid SchemaGuid = new Guid("e6a0c4f3-9b5d-6ebf-d7f8-3c4a5b6e7f80");
        private const string SchemaName = "TurboZonesPanelSettingsV3";
        private const string BrandField = "Brand";
        private const string SpecialKeysField = "SpecialDeviceKeys";
        private const string SpecialValuesField = "SpecialDeviceValues";

        // V2 schema GUID for migration
        private static readonly Guid V2SchemaGuid = new Guid("d5f9b3e2-8a4c-5daf-c6e7-2b3f4a5d6e7f");
        // V1 schema GUID for migration
        private static readonly Guid V1SchemaGuid = new Guid("c4e8a2d1-7f3b-4c9e-b5d6-1a2e3f4b5c6d");

        private static Schema GetOrCreateSchema()
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName(SchemaName);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(BrandField, typeof(string));
            builder.AddArrayField(SpecialKeysField, typeof(string));
            builder.AddArrayField(SpecialValuesField, typeof(string));
            return builder.Finish();
        }

        private static DataStorage FindDataStorage(Document doc, Schema schema)
        {
            using (var collector = new FilteredElementCollector(doc))
            {
                return collector
                    .OfClass(typeof(DataStorage))
                    .Cast<DataStorage>()
                    .FirstOrDefault(ds => ds.GetEntity(schema).IsValid());
            }
        }

        public static PanelSettings Load(Document doc)
        {
            // Try V3 schema first
            var schema = Schema.Lookup(SchemaGuid);
            if (schema != null)
            {
                var storage = FindDataStorage(doc, schema);
                if (storage != null)
                {
                    var entity = storage.GetEntity(schema);
                    if (entity.IsValid())
                        return LoadFromEntity(entity, schema);
                }
            }

            // Fall back to V2 schema (has size overrides we ignore)
            var v2Schema = Schema.Lookup(V2SchemaGuid);
            if (v2Schema != null)
            {
                var v2Storage = FindDataStorage(doc, v2Schema);
                if (v2Storage != null)
                {
                    var v2Entity = v2Storage.GetEntity(v2Schema);
                    if (v2Entity.IsValid())
                        return LoadBrandAndDevicesFromEntity(v2Entity);
                }
            }

            // Fall back to V1 schema
            var v1Schema = Schema.Lookup(V1SchemaGuid);
            if (v1Schema != null)
            {
                var v1Storage = FindDataStorage(doc, v1Schema);
                if (v1Storage != null)
                {
                    var v1Entity = v1Storage.GetEntity(v1Schema);
                    if (v1Entity.IsValid())
                        return LoadBrandAndDevicesFromEntity(v1Entity);
                }
            }

            return null;
        }

        private static PanelSettings LoadFromEntity(Entity entity, Schema schema)
        {
            var settings = new PanelSettings
            {
                Brand = entity.Get<string>(BrandField)
            };

            var keys = entity.Get<IList<string>>(SpecialKeysField);
            var values = entity.Get<IList<string>>(SpecialValuesField);
            if (keys != null && values != null)
            {
                for (int i = 0; i < Math.Min(keys.Count, values.Count); i++)
                    settings.SpecialDeviceSelections[keys[i]] = values[i];
            }

            return settings;
        }

        private static PanelSettings LoadBrandAndDevicesFromEntity(Entity entity)
        {
            var settings = new PanelSettings
            {
                Brand = entity.Get<string>("Brand")
            };

            try
            {
                var keys = entity.Get<IList<string>>("SpecialDeviceKeys");
                var values = entity.Get<IList<string>>("SpecialDeviceValues");
                if (keys != null && values != null)
                {
                    for (int i = 0; i < Math.Min(keys.Count, values.Count); i++)
                        settings.SpecialDeviceSelections[keys[i]] = values[i];
                }
            }
            catch { /* field missing in older schema */ }

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
                entity.Set(SpecialKeysField, (IList<string>)settings.SpecialDeviceSelections.Keys.ToList());
                entity.Set(SpecialValuesField, (IList<string>)settings.SpecialDeviceSelections.Values.ToList());
                storage.SetEntity(entity);

                tx.Commit();
            }
        }
    }
}
