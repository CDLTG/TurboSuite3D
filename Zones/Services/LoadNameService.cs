#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    public class LoadNameService
    {
        public int UpdateLoadNames(Document doc, List<ZonesCircuitData> circuits)
        {
            int updatedCount = 0;

            using (var trans = new Transaction(doc, "TurboZones - Update Load Names"))
            {
                trans.Start();

                foreach (var circuitData in circuits)
                {
                    Element element = doc.GetElement(circuitData.CircuitId);
                    if (element is not ElectricalSystem circuit)
                        continue;

                    bool updated = false;

                    // Write Updated Load Name to RBS_ELEC_CIRCUIT_NAME
                    if (!string.IsNullOrWhiteSpace(circuitData.UpdatedLoadName))
                    {
                        Parameter loadNameParam = circuit.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NAME);
                        if (loadNameParam != null && !loadNameParam.IsReadOnly)
                        {
                            loadNameParam.Set(circuitData.UpdatedLoadName);
                            updated = true;
                        }
                    }

                    // Write Circuit Comments to ALL_MODEL_INSTANCE_COMMENTS
                    Parameter commentsParam = circuit.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (commentsParam != null && !commentsParam.IsReadOnly)
                    {
                        commentsParam.Set(circuitData.CircuitComments ?? string.Empty);
                        updated = true;
                    }

                    if (updated)
                        updatedCount++;
                }

                trans.Commit();
            }

            return updatedCount;
        }
    }
}
