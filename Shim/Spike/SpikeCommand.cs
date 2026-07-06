#nullable disable
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TurboSuite.Spike;

/// <summary>
/// TurboSpike — the throwaway diagnostic bench.
///
/// PURPOSE: an always-available (in dev) ribbon button whose <see cref="Execute"/> body is meant to be
/// SWAPPED per-investigation. When you need to know something about the running model before writing
/// targeted code — what a parameter's StorageType actually is, whether an API member exists on this
/// Revit version, what a family's connectors/geometry look like — drop a probe here, build, and read
/// the dialog.
///
/// STATE: rides the shared <c>ExperimentalCommandsEnabled</c> gate in <see cref="App.TurboSuiteApplication"/>,
/// so it surfaces every dev session and is gated off in shipped builds — it never reaches production users.
///
/// Keep this ReadOnly and side-effect-free by default. If a probe needs to write, wrap it in a Transaction
/// and change the attribute for the duration of that spike — then revert. This file is scratch space; the
/// body below is only a sensible starting probe, not something to preserve.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class SpikeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc?.Document == null)
        {
            TaskDialog.Show("TurboSpike", "No active document.");
            return Result.Cancelled;
        }

        Document doc = uidoc.Document;
        View view = doc.ActiveView;
        var selectedIds = uidoc.Selection.GetElementIds();

        var sb = new StringBuilder();
        sb.AppendLine($"Document: {doc.Title}");
        sb.AppendLine($"Revit version: {commandData.Application.Application.VersionNumber}");
        sb.AppendLine($"Active view: {view.Name}  ({view.ViewType})");
        sb.AppendLine($"Selection count: {selectedIds.Count}");

        // Default probe: dump the picked element's parameters (name / StorageType / value) so you can
        // see exactly what's writable and how it's stored before coding against it. Swap as needed.
        if (selectedIds.Count > 0)
        {
            Element el = doc.GetElement(selectedIds.First());
            sb.AppendLine();
            sb.AppendLine($"First selected: {el.Category?.Name} — {el.Name}  (Id {el.Id})");
            foreach (Parameter p in el.Parameters.Cast<Parameter>()
                         .OrderBy(p => p.Definition?.Name))
            {
                string value = p.StorageType switch
                {
                    StorageType.String => p.AsString(),
                    StorageType.Integer => p.AsInteger().ToString(),
                    StorageType.Double => p.AsValueString(),
                    StorageType.ElementId => p.AsElementId().ToString(),
                    _ => "?"
                };
                sb.AppendLine($"  {p.Definition?.Name} [{p.StorageType}]{(p.IsReadOnly ? " (ro)" : "")} = {value}");
            }
        }

        TaskDialog.Show("TurboSpike", sb.ToString());
        return Result.Succeeded;
    }
}
