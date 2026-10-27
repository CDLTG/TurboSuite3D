#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Number.Models;
using TurboSuite.Number.ViewModels;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Services;

namespace TurboSuite.Number.Services
{
    public class NumberWriterService
    {
        public void WritePanelSettings(Document doc, IReadOnlyList<PanelSettingsModel> panelSettings)
        {
            int updated = 0;
            int skipped = 0;

            if (!NumberWorksharing.Lease(doc, panelSettings.Select(p => p.PanelElementId.ToElementId()), "panel"))
                return;

            using (Transaction tx = new Transaction(doc, "TurboNumber - Write Panel Settings"))
            {
                tx.Start();

                foreach (var panel in panelSettings)
                {
                    Element panelEl = doc.GetElement(panel.PanelElementId.ToElementId());
                    if (panelEl == null) { skipped++; continue; }

                    ParameterHelper.SetCircuitNaming(panelEl, panel.CircuitNaming);
                    ParameterHelper.SetCircuitPrefix(panelEl, panel.CircuitPrefix);
                    ParameterHelper.SetCircuitPrefixSeparator(panelEl, panel.CircuitPrefixSeparator);
                    updated++;
                }

                if (!NumberWorksharing.ReportIfRolledBack(CommitGuard.TryCommit(tx))) return;
            }

            TaskDialog.Show("TurboNumber", $"Panel Settings: {updated} updated, {skipped} skipped.");
        }

        public void WriteDeviceSwitchIds(Document doc, IList<NumberableRowViewModel> rows)
        {
            int updated = 0;
            int skipped = 0;

            if (!NumberWorksharing.Lease(doc, rows.Select(r => r.ElementId.ToElementId()), "device"))
                return;

            using (Transaction tx = new Transaction(doc, "TurboNumber - Write Switch IDs"))
            {
                tx.Start();

                foreach (var row in rows)
                {
                    Element el = doc.GetElement(row.ElementId.ToElementId());
                    if (el == null) { skipped++; continue; }

                    Parameter param = el.LookupParameter(ParameterNames.SwitchId);
                    if (param == null || param.IsReadOnly)
                    {
                        skipped++;
                        continue;
                    }

                    param.Set(row.Value);
                    updated++;
                }

                if (!NumberWorksharing.ReportIfRolledBack(CommitGuard.TryCommit(tx))) return;
            }

            TaskDialog.Show("TurboNumber", $"Switch IDs: {updated} updated, {skipped} skipped.");
        }
    }
}
