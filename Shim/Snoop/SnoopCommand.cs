#nullable disable
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using TurboSuite.Shared.Services;
using TurboSuite.Snoop.Models;
using TurboSuite.Snoop.Services;
using TurboSuite.Snoop.ViewModels;
using TurboSuite.Snoop.Views;
using RvtOperationCanceled = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace TurboSuite.Snoop;

/// <summary>
/// TurboSnoop — read-only "which VG checkbox do I uncheck?" reporter for arch-link geometry.
///
/// PURPOSE: clearance / path / egress lines (and other content) ride inside deeply nested linked-model
/// families, and finding the right Category/Subcategory to uncheck in VG → RVT Links → Custom by hand is slow
/// trial-and-error. TurboSnoop picks a linked family and lists every VG checkbox its geometry draws under. The
/// user does the single uncheck — being modeless, the Revit view keeps its VG/VV keybind with the window open.
///
/// STATE: shipped (ribbon still uses the `Blank` placeholder icon pending a dedicated one). The pick is
/// synchronous here (before the window exists), so no external-event work queue.
///
/// DELIBERATELY A FINDER, NOT A HIDER — there is no API to flip the VG checkbox this tool names, so do NOT add
/// an "Apply":
///   • Individual linked ELEMENTS: View.SetElementOverrides takes only a host-doc ElementId (no linked
///     overload); the sole "hide" path is the async Reference.CreateLinkReference + PostCommand(HideElements).
///   • Per-link CATEGORY/subcategory overrides (VG → RVT Links → Custom): RevitLinkGraphicsSettings exposes
///     only whole-link knobs (ObjectStyles/ColorFill/ViewRange/LinkedViewId) — no per-category setter — and
///     Custom isn't even settable in the 2024 API. Host-view View.SetCategoryHidden can drive a link under
///     ObjectStyles=ByHostView, but only for categories that exist in the HOST doc, and host-view-wide; the
///     link-DEFINED subcategories this tool exists to find (e.g. a nested "x_DI_Clearance") aren't reachable.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class SnoopCommand : IExternalCommand
{
    private static TurboSnoopWindow _activeWindow;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIApplication uiapp = commandData.Application;
        UIDocument uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
        {
            TaskDialog.Show("TurboSnoop", "No active document found.");
            return Result.Failed;
        }

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

        string header = LinkedGeometryTreeBuilder.DescribeFamily(linkedElement);
        SnoopNode root = new LinkedGeometryTreeBuilder().BuildUnion(linkedElement, linkedDoc);
        SnoopNodeViewModel rootVm = SnoopNodeViewModel.BuildTree(root);

        // One window at a time — a fresh pick replaces the previous report.
        _activeWindow?.Close();

        var window = new TurboSnoopWindow { DataContext = new SnoopMainViewModel(header, rootVm) };
        new WindowInteropHelper(window) { Owner = uiapp.MainWindowHandle };
        window.Closed += (s, e) =>
        {
            if (ReferenceEquals(_activeWindow, window))
                _activeWindow = null;
        };

        ModelessWindowGuard.Register(doc, window, window.Close);
        _activeWindow = window;
        window.Show();
        return Result.Succeeded;
    }
}
