#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Services;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    public class LoadNameService
    {
        public int UpdateLoadNames(Document doc, List<ZonesCircuitData> circuits)
        {
            int updatedCount = 0;

            // Per-circuit room overrides to persist (UniqueId → override). Built from
            // the full snapshot so cleared/removed overrides are pruned on write.
            var roomOverrides = new Dictionary<string, string>();

            using (var trans = new Transaction(doc, "TurboZones - Update Load Names"))
            {
                trans.Start();

                foreach (var circuitData in circuits)
                {
                    Element element = doc.GetElement(circuitData.CircuitId.ToElementId());
                    if (element is not ElectricalSystem circuit)
                        continue;

                    bool updated = false;

                    // Load Name
                    if (!string.IsNullOrWhiteSpace(circuitData.UpdatedLoadName))
                    {
                        Parameter loadNameParam = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NAME);
                        if (loadNameParam != null && !loadNameParam.IsReadOnly)
                        {
                            loadNameParam.Set(circuitData.UpdatedLoadName);
                            updated = true;
                        }
                    }

                    // Circuit Comments
                    Parameter commentsParam = circuit.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (commentsParam != null && !commentsParam.IsReadOnly)
                    {
                        commentsParam.Set(circuitData.CircuitComments ?? string.Empty);
                        updated = true;
                    }

                    // Room override → persisted per-circuit (keyed by UniqueId), kept
                    // separate from any room-name source so it never bleeds to the region.
                    if (!string.IsNullOrWhiteSpace(circuitData.RoomOverride))
                        roomOverrides[circuit.UniqueId] = circuitData.RoomOverride;

                    if (updated)
                        updatedCount++;
                }

                RoomOverrideStorageService.Write(doc, roomOverrides);

                trans.Commit();
            }

            return updatedCount;
        }
    }
}
