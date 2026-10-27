#nullable disable
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Shared.Services;

namespace TurboSuite.Number.Services
{
    /// <summary>
    /// TurboNumber's two worksharing touchpoints. The window holds a session lease on its lock and data stores
    /// (see <see cref="NumberCommand"/>); circuits, panels, schedule views and devices are hot, so each write
    /// borrows just its own elements here, per operation, and runs nothing unless all of them are held. Every
    /// commit is honored — a rolled-back save is reported, never shown as saved.
    /// </summary>
    internal static class NumberWorksharing
    {
        /// <summary>Borrow one operation's working set; on refusal show why and return false.</summary>
        public static bool Lease(Document doc, IEnumerable<ElementId> workingSet, string itemNoun)
        {
            var lease = WorksharingLease.Acquire(doc, workingSet, itemNoun);
            if (lease.IsGranted) return true;
            TaskDialog.Show("TurboNumber", lease.DenialMessage);
            return false;
        }

        /// <summary>Surface a rolled-back commit. Returns <paramref name="committed"/>.</summary>
        public static bool ReportIfRolledBack(bool committed)
        {
            if (!committed) TaskDialog.Show("TurboNumber", WorksharingMessages.RolledBack);
            return committed;
        }
    }
}
