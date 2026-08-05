#nullable disable
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TurboSuite.Spike;

/// <summary>
/// TurboSpike — the throwaway diagnostic bench. See the class rules in CLAUDE.md; clobber freely.
///
/// No active probe. When the running model can answer a question you'd otherwise guess at
/// (a parameter's StorageType/writability, whether an API member exists on this version, a family's
/// connectors/geometry/placement behavior), write a probe into Execute, have the user build and run
/// it, and read the dialog — then clear it back to this stub.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class SpikeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        TaskDialog.Show("TurboSpike", "No active probe.");
        return Result.Succeeded;
    }
}
