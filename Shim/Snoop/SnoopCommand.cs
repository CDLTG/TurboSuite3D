#nullable disable
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using TurboSuite.Shared.Hosting;
using TurboSuite.Shared.Services;
using TurboSuite.Snoop.Models;
using TurboSuite.Snoop.Services;
using TurboSuite.Snoop.ViewModels;
using TurboSuite.Snoop.Views;
using RvtOperationCanceled = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace TurboSuite.Snoop;

/// <summary>
/// TurboSnoop — read-only reporter for the two "what is this connected to?" questions that Revit hides.
/// Selection-aware, single window, two branches off one button:
///
///   • YOUR OWN element(s) selected → HOST report: what is this hosted to? (HostResolutionService →
///     HostReportTree). Answers "why is this behaving strangely?" for a keypad/fixture you're staring
///     at — surfaces a link-hosted casework/stairs host (churn/orphan risk) or an already-orphaned host
///     that Revit shows only as "hosted to <the link>".
///   • NOTHING selected → pick a LINKED element → VG report: which VG checkbox do I uncheck to hide it?
///     (LinkedGeometryTreeBuilder — the original TurboSnoop). Modeless so the Revit VG/VV keybind stays live.
///
/// Why selection-aware and not one unified pick: no PickObject ObjectType accepts both a host-doc element
/// AND a nested linked sub-element — LinkedElement mode rejects your own elements, Element mode collapses
/// a link click to the whole RevitLinkInstance (no nested family for the VG walk). So the natural split is
/// "own element already selected → host; else pick into a link → VG." Escape: deselect to reach the VG path.
///
/// DELIBERATELY A FINDER, NOT A HIDER (VG branch) — there is no API to flip the VG checkbox this tool
/// names. See the rejected-alternatives note that used to head this file, preserved on LinkedGeometryTreeBuilder.
/// The host branch is likewise read-only — it names the host and its risk; re-hosting stays a manual act.
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

        // ── Selection-aware branch: your own element(s) selected → host report, no pick needed. ──
        // (A RevitLinkInstance is not a FamilyInstance, so a selected whole-link falls through to VG.)
        List<FamilyInstance> ownSelected = uidoc.Selection.GetElementIds()
            .Select(doc.GetElement)
            .OfType<FamilyInstance>()
            .ToList();

        if (ownSelected.Count > 0)
            return ShowHostReport(uiapp, doc, ownSelected);

        // ── Otherwise: pick a linked element → VG-checkbox report (the original TurboSnoop). ──
        return ShowVgReport(uiapp, uidoc, doc);
    }

    private Result ShowHostReport(UIApplication uiapp, Document doc, List<FamilyInstance> ownSelected)
    {
        // Single-object report: the first selected own element (multi-selection is the future full audit).
        FamilyInstance target = ownSelected[0];
        HostResolution res = HostResolutionService.ResolveOne(target, doc);

        string header = ownSelected.Count > 1
            ? $"{res.PickedLabel}   (first of {ownSelected.Count} selected)"
            : res.PickedLabel;

        SnoopNode root = HostReportTree.Build(res);
        ShowWindow(uiapp, doc, header, SnoopNodeViewModel.BuildTree(root));
        return Result.Succeeded;
    }

    private Result ShowVgReport(UIApplication uiapp, UIDocument uidoc, Document doc)
    {
        Reference reference;
        try
        {
            reference = uidoc.Selection.PickObject(
                ObjectType.LinkedElement,
                "TurboSnoop: pick a linked family to list the VG checkboxes its geometry draws under "
                + "(or select your own element first to snoop its host).");
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
        ShowWindow(uiapp, doc, header, SnoopNodeViewModel.BuildTree(root));
        return Result.Succeeded;
    }

    /// <summary>Opens (or replaces) the single modeless TurboSnoop window over the given report tree.</summary>
    private void ShowWindow(UIApplication uiapp, Document doc, string header, SnoopNodeViewModel rootVm)
    {
        // One window at a time — a fresh run replaces the previous report.
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
    }
}
