#nullable disable
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
/// body below is only a clean stub — overwrite it freely with whatever probe the current investigation needs.
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

        // No probe loaded — drop diagnostics for the current investigation into this body, build, and read
        // the dialog. See the class doc comment / CLAUDE.md "TurboSpike — Diagnostic Bench".
        TaskDialog.Show("TurboSpike",
            $"No probe loaded.\n\n" +
            $"Document: {doc.Title}\n" +
            $"Revit version: {commandData.Application.Application.VersionNumber}\n" +
            $"Active view: {view.Name} ({view.ViewType})");

        return Result.Succeeded;
    }
}
