using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using TurboSuite.Shared.Helpers;
using RvtOperationCanceled = Autodesk.Revit.Exceptions.OperationCanceledException;

namespace TurboSuite.Nudge;

/// <summary>
/// TurboNudge — slides a point-based family (typically a keypad) along its wall so it sits exactly
/// a fixed offset (5") from a picked corner. Replaces the manual "place roughly, then dimension and
/// set the witness distance to 5" flow.
///
/// The mechanic needs neither the host wall nor a CAD line: the family already carries its along-wall
/// direction in its transform, so the same code covers hosted (3D) and unhosted (2D) families.
///
///   wallDir = (cosθ, sinθ, 0),  θ = Atan2(BasisX.Y, BasisX.X)   — the project's blessed direction rule
///   d       = (current − corner) · wallDir                      — signed distance along the wall
///   move by  wallDir · (sign(d)·offset − d)                     — slide to `offset` on the SAME side
///
/// Only the along-wall component moves (delta is horizontal and parallel to wallDir), so the family's
/// elevation and its perpendicular offset from the wall are untouched — it just lands 5" from the corner
/// on whichever side it was already placed. That "same side" rule is the least-surprising behavior:
/// wherever you dropped the keypad decides which way the 5" goes.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class NudgeCommand : IExternalCommand
{
    /// <summary>Offset from the picked corner, in feet (Revit's internal unit). 5 inches.</summary>
    private const double OffsetFeet = 5.0 / 12.0;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc?.Document == null)
        {
            TaskDialog.Show("TurboNudge", "No active document found.");
            return Result.Cancelled;
        }

        Document doc = uidoc.Document;

        if (!IsPlanView(doc.ActiveView))
        {
            TaskDialog.Show("TurboNudge", "Run TurboNudge from a Floor Plan or Reflected Ceiling Plan view.");
            return Result.Cancelled;
        }

        try
        {
            // The keypad: use a single pre-selected point family if there is one (place → nudge in one
            // keystroke), otherwise prompt for one.
            FamilyInstance? keypad = GetPreselectedKeypad(uidoc, doc)
                ?? PickKeypad(uidoc);
            if (keypad == null)
                return Result.Cancelled;

            if (keypad.Location is not LocationPoint locationPoint)
            {
                TaskDialog.Show("TurboNudge", "That family has no point location to move.");
                return Result.Cancelled;
            }

            // The corner: snap to the door jamb / frame corner. Endpoint + intersection + point snaps
            // catch the corner on both linked model geometry (3D) and CAD lines (2D).
            XYZ corner = uidoc.Selection.PickPoint(
                ObjectSnapTypes.Endpoints | ObjectSnapTypes.Intersections | ObjectSnapTypes.Points,
                "Snap to the door corner to place the keypad 5\" from it (Esc to cancel)");

            double angle = GeometryHelper.GetTransformAngle(keypad.GetTransform());
            XYZ wallDir = new XYZ(Math.Cos(angle), Math.Sin(angle), 0);

            XYZ current = locationPoint.Point;
            double d = (current - corner).DotProduct(wallDir);
            double sign = d < 0 ? -1.0 : 1.0; // keep the keypad on the side it's already on
            XYZ delta = wallDir * (sign * OffsetFeet - d);

            using (Transaction tx = new Transaction(doc, "TurboNudge — 5\" from corner"))
            {
                tx.Start();
                ElementTransformUtils.MoveElement(doc, keypad.Id, delta);
                tx.Commit();
            }

            return Result.Succeeded;
        }
        catch (RvtOperationCanceled)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("TurboNudge", $"Could not nudge the keypad:\n{ex.Message}");
            return Result.Failed;
        }
    }

    private static bool IsPlanView(View view) =>
        view != null &&
        (view.ViewType == ViewType.FloorPlan ||
         view.ViewType == ViewType.CeilingPlan ||
         view.ViewType == ViewType.EngineeringPlan ||
         view.ViewType == ViewType.AreaPlan);

    /// <summary>Returns the sole pre-selected point-based family instance, or null if the selection
    /// isn't exactly one such element (0, several, or a non-point family — all fall through to a pick).</summary>
    private static FamilyInstance? GetPreselectedKeypad(UIDocument uidoc, Document doc)
    {
        var ids = uidoc.Selection.GetElementIds();
        if (ids.Count != 1)
            return null;

        Element? element = null;
        foreach (ElementId id in ids)
            element = doc.GetElement(id);

        return element is FamilyInstance fi && fi.Location is LocationPoint ? fi : null;
    }

    private static FamilyInstance? PickKeypad(UIDocument uidoc)
    {
        Reference reference = uidoc.Selection.PickObject(
            ObjectType.Element, new PointFamilyFilter(),
            "Select the keypad to nudge (Esc to cancel)");
        return uidoc.Document.GetElement(reference) as FamilyInstance;
    }

    /// <summary>Accepts any point-based family instance — keypads are Lighting Devices, but the nudge is
    /// generic, so anything with a LocationPoint qualifies.</summary>
    private class PointFamilyFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) =>
            elem is FamilyInstance fi && fi.Location is LocationPoint;

        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}
