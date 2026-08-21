#nullable disable
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TurboSuite.Spike;

/// <summary>
/// TurboSpike — the throwaway diagnostic bench. See the class rules in CLAUDE.md.
///
/// Overwrite-safe by design: everything in <see cref="Execute"/> is diagnostics-only scratch. When
/// you need to answer a question the running model can settle (a parameter's StorageType/writability,
/// whether an API member exists on this version, a family's connectors/geometry), clobber whatever
/// stub is here with a probe, have the user build and run it, and read the dialog. No prior spike is
/// worth preserving. It ships gated behind ExperimentalCommandsEnabled, so it's dev-only.
///
/// CURRENT PROBE — none. This is the idle stub: it just reports the active selection. Overwrite the
/// <see cref="Execute"/> body with your probe when you need one.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SpikeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uidoc = commandData.Application.ActiveUIDocument;
        var selIds = uidoc.Selection.GetElementIds().ToList();

        TaskDialog.Show("TurboSpike",
            $"No probe loaded. Active selection: {selIds.Count} element(s).\n\n"
            + "Clobber SpikeCommand.Execute with a probe to answer an in-model question.");

        return Result.Succeeded;
    }
}
