#nullable disable
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Number.Models;
using TurboSuite.Number.ViewModels;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Number.Services
{
    public class NumberWriterService
    {
        public void WritePanelSettings(Document doc, IList<PanelSettingsModel> panelSettings)
        {
            int updated = 0;
            int skipped = 0;

            using (Transaction tx = new Transaction(doc, "TurboNumber - Write Panel Settings"))
            {
                tx.Start();

                foreach (var panel in panelSettings)
                {
                    Element panelEl = doc.GetElement(panel.PanelElementId);
                    if (panelEl == null) { skipped++; continue; }

                    ParameterHelper.SetCircuitNaming(panelEl, panel.CircuitNaming);
                    ParameterHelper.SetCircuitPrefix(panelEl, panel.CircuitPrefix);
                    ParameterHelper.SetCircuitPrefixSeparator(panelEl, panel.CircuitPrefixSeparator);
                    updated++;
                }

                tx.Commit();
            }

            TaskDialog.Show("TurboNumber", $"Panel Settings: {updated} updated, {skipped} skipped.");
        }

        public void WriteDeviceSwitchIds(Document doc, IList<NumberableRowViewModel> rows)
        {
            int updated = 0;
            int skipped = 0;

            using (Transaction tx = new Transaction(doc, "TurboNumber - Write Switch IDs"))
            {
                tx.Start();

                foreach (var row in rows)
                {
                    Element el = doc.GetElement(row.ElementId);
                    if (el == null) { skipped++; continue; }

                    Parameter param = el.LookupParameter("Switch ID");
                    if (param == null || param.IsReadOnly)
                    {
                        skipped++;
                        continue;
                    }

                    param.Set(row.Value);
                    updated++;
                }

                tx.Commit();
            }

            TaskDialog.Show("TurboNumber", $"Switch IDs: {updated} updated, {skipped} skipped.");
        }
    }
}
