#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using TurboSuite.Shared.Services;

namespace TurboSuite.Zones.Services
{
    public class PanelSettings
    {
        public string Brand { get; set; }
        public Dictionary<string, string> SpecialDeviceSelections { get; set; } = new Dictionary<string, string>();
    }

    public static class ZonesPanelSettingsStorageService
    {
        private static readonly Guid SchemaGuid = new Guid("e6a0c4f3-9b5d-6ebf-d7f8-3c4a5b6e7f80");
        private const string SchemaName = "TurboZonesPanelSettings";
        private const string BrandField = "Brand";
        private const string SpecialKeysField = "SpecialDeviceKeys";
        private const string SpecialValuesField = "SpecialDeviceValues";

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
