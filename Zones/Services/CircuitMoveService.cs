#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Executes circuit redistribution moves in Revit via DisconnectPanel/SelectPanel.
    /// </summary>
    public class CircuitMoveService
    {
        /// <summary>
        /// Executes all circuit moves from the redistribution plan in a single transaction.
        /// Returns the set of CircuitIds that were successfully moved.
        /// If SelectPanel fails after DisconnectPanel, the circuit is reconnected
        /// to its original panel to avoid orphaning.
        /// </summary>
        public HashSet<ElementId> ApplyPlan(Document doc, RedistributionPlan plan)
        {
            var movedIds = new HashSet<ElementId>();

            if (plan == null || !plan.HasChanges)
                return movedIds;

            var panelMap = BuildPanelMap(doc);

            using (var tx = new Transaction(doc, "TurboZones - Optimize Circuit Distribution"))
            {
                tx.Start();

                foreach (var move in plan.Moves)
                {
                    if (!panelMap.TryGetValue(move.ToPanel, out ElementId targetPanelId))
                        continue;

                    if (doc.GetElement(move.CircuitId) is not ElectricalSystem circuit)
                        continue;

                    var targetPanel = doc.GetElement(targetPanelId) as FamilyInstance;
                    if (targetPanel == null)
                        continue;

                    // Capture the original panel before disconnecting
                    FamilyInstance originalPanel = circuit.BaseEquipment;

                    try
                    {
                        if (originalPanel != null)
                            circuit.DisconnectPanel();

                        circuit.SelectPanel(targetPanel);
                        movedIds.Add(move.CircuitId);
                    }
                    catch
                    {
                        // SelectPanel failed after DisconnectPanel — reconnect to original
                        if (originalPanel != null)
                        {
                            try
                            {
                                circuit.SelectPanel(originalPanel);
                            }
                            catch
                            {
                                // Reconnect also failed — circuit is orphaned.
                                // This shouldn't happen in practice since the original
                                // panel was valid moments ago.
                            }
                        }
                    }
                }

                tx.Commit();
            }

            return movedIds;
        }

        private static Dictionary<string, ElementId> BuildPanelMap(Document doc)
        {
            var map = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);

            var panels = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>();

            foreach (var panel in panels)
            {
                string name = panel.Name;
                if (!string.IsNullOrWhiteSpace(name) && !map.ContainsKey(name))
                    map[name] = panel.Id;
            }

            return map;
        }
    }
}
