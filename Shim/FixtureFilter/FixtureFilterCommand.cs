using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TurboSuite.FixtureFilter;

/// <summary>
/// TurboFixtureFilter — one keystroke narrows the current selection to Lighting Fixtures, standing in for
/// the native Modify → Filter dialog's "Check None, tick Lighting Fixtures, OK".
///
/// Selection-only: no model change, so no transaction and no undo entry (same as the native dialog).
/// A selection with no Lighting Fixtures is left untouched rather than cleared — a keystroke that wipes
/// a hand-built selection is worse than one that reports it did nothing.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class FixtureFilterCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc?.Document == null)
        {
            TaskDialog.Show("TurboFixtureFilter", "No active document found.");
            return Result.Cancelled;
        }

        Document doc = uidoc.Document;
        ICollection<ElementId> selected = uidoc.Selection.GetElementIds();
        if (selected.Count == 0)
        {
            TaskDialog.Show("TurboFixtureFilter", "Select elements first, then run TurboFixtureFilter to keep only the Lighting Fixtures.");
            return Result.Cancelled;
        }

        List<ElementId> fixtures = selected
            .Where(id => doc.GetElement(id)?.Category?.BuiltInCategory == BuiltInCategory.OST_LightingFixtures)
            .ToList();

        if (fixtures.Count == 0)
        {
            TaskDialog.Show("TurboFixtureFilter", "The selection contains no Lighting Fixtures — selection left unchanged.");
            return Result.Cancelled;
        }

        uidoc.Selection.SetElementIds(fixtures);
        return Result.Succeeded;
    }
}
