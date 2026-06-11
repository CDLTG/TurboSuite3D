#nullable disable
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using TurboSuite.Snoop.Models;
using TurboSuite.Snoop.Services;
using RvtOperationCanceled = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace TurboSuite.Snoop;

/// <summary>
/// TurboSnoop — read-only "which VG checkbox do I uncheck?" reporter for arch-link geometry.
///
/// PURPOSE: clearance / path / egress lines (and other content) ride inside deeply nested linked-model
/// families, and finding the right Category/Subcategory to uncheck in VG → RVT Links → Custom by hand is slow
/// trial-and-error. TurboSnoop picks a linked family and lists every VG checkbox its geometry draws under,
/// grouped to mirror the VG dialog (Section → Category → Subcategory). The user does the single uncheck.
///
/// WORKFLOW: run → PickObject(LinkedElement) → resolve via reference.LinkedElementId +
/// RevitLinkInstance.GetLinkDocument() → <see cref="LinkedGeometryTreeBuilder.BuildUnion"/> → TaskDialog dump.
///
/// STATE: gated SPIKE (ExperimentalCommandsEnabled, like TurboMask/TurboSetup) — compiled but unreachable in
/// production. Output is a TaskDialog text dump; the v1 UI will be a modeless WPF TreeView binding the
/// <c>SnoopNode</c> tree (its Family/Category/Subcategory/Info kinds already carry what it needs). Pure read,
/// no transaction.
///
/// DELIBERATELY A FINDER, NOT A HIDER. There is no API to hide an individual linked element even in Revit
/// 2025: View.SetElementOverrides takes only a host-doc ElementId (no linked overload), and the sole "hide"
/// path is the async, non-transactional Reference.CreateLinkReference + PostCommand(HideElements) UI
/// workaround. So TurboSnoop reports the checkbox and the user unchecks it.
///
/// OUT OF SCOPE (revisit when expanding): instance parameters / identity panel, RevitLookup-style full
/// property snoop, programmatic hiding. Subcategory-toggle granularity (coincident geometry in the same
/// subcategory toggles together) is accepted, not a problem to solve.
///
/// DESIGN REFERENCE ONLY (nothing vendored): RevitLookup + LookupEngine (both MIT) for the reflection / tree-UI
/// pattern — re-build a small TurboSuite-native lookup rather than fork their DI/hosting-heavy app (referencing
/// the design, not shipping the code, means no MIT-attribution obligation).
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class SnoopCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        Reference reference;
        try
        {
            reference = uidoc.Selection.PickObject(
                ObjectType.LinkedElement,
                "TurboSnoop: pick a linked family to list the VG checkboxes its geometry draws under.");
        }
        catch (RvtOperationCanceled)
        {
            return Result.Cancelled;
        }

        if (doc.GetElement(reference.ElementId) is not RevitLinkInstance linkInstance)
        {
            TaskDialog.Show("TurboSnoop", "The picked element is not a linked instance.");
            return Result.Cancelled;
        }

        Document linkedDoc = linkInstance.GetLinkDocument();
        if (linkedDoc == null)
        {
            TaskDialog.Show("TurboSnoop", "The linked document is not loaded.");
            return Result.Cancelled;
        }

        Element linkedElement = linkedDoc.GetElement(reference.LinkedElementId);
        if (linkedElement == null)
        {
            TaskDialog.Show("TurboSnoop", "Could not resolve the picked linked element.");
            return Result.Cancelled;
        }

        SnoopNode root = new LinkedGeometryTreeBuilder().BuildUnion(linkedElement, linkedDoc);
        string dump = SnoopTreeFormatter.ToIndentedText(root);

        var td = new TaskDialog("TurboSnoop (spike)")
        {
            MainInstruction = "Visibility/Graphics checkboxes for this linked element",
            MainContent = "Two sections: Model geometry (reliable, hierarchical — glass, parts, nested "
                + "families) and View-dependent / annotation (detail items, masking, symbolic lines). Each "
                + "'•' is a Category → Subcategory to uncheck in VG → RVT Links → Custom.",
            ExpandedContent = dump,
        };
        td.Show();

        return Result.Succeeded;
    }
}
