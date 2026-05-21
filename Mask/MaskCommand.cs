using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Mask.Services;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Mask;

/// <summary>
/// TurboMask — places a project-level masking region under the selected elements and overlays a
/// view-level "stamp" (extracted from each fixture family's nested Generic Annotation) at every
/// selected fixture so the visible footprint graphics survive the mask. See Specs/TurboMask-Plan.md.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class MaskCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;
        View activeView = doc.ActiveView;

        try
        {
            if (!IsValidViewType(activeView))
            {
                TaskDialog.Show("TurboMask", "TurboMask requires a plan, RCP, or drafting view.");
                return Result.Cancelled;
            }

            var selectedIds = uidoc.Selection.GetElementIds();
            if (selectedIds.Count == 0)
            {
                TaskDialog.Show("TurboMask", "Select elements before running TurboMask.");
                return Result.Cancelled;
            }

            var selectedElements = selectedIds
                .Select(id => doc.GetElement(id))
                .Where(e => e != null)
                .ToList();

            var fixtures = CollectLightingFixtures(selectedElements);

            // EditFamily must run OUTSIDE any transaction — resolve stamps first.
            var stampService = new StampFamilyService(doc);
            var failures = new List<string>();
            var fixtureToSymbol = new Dictionary<ElementId, FamilySymbol>();

            foreach (var fixture in fixtures)
            {
                var family = fixture.Symbol?.Family;
                if (family == null) continue;

                var symbol = stampService.ResolveStamp(family, failures);
                if (symbol != null)
                    fixtureToSymbol[fixture.Id] = symbol;
            }

            var outerLoop = SelectionBoundsService.BuildOuterLoop(selectedElements, activeView);
            if (outerLoop == null)
            {
                TaskDialog.Show("TurboMask", "Selected elements have no usable bounds in this view.");
                return Result.Cancelled;
            }

            using (var tx = new Transaction(doc, "TurboMask"))
            {
                tx.Start();

                var maskingRegionType = FindOrCreateMaskingRegionType(doc);
                if (maskingRegionType == null)
                {
                    tx.RollBack();
                    TaskDialog.Show("TurboMask",
                        "Could not create a masking region type — the project has no FilledRegionType to duplicate.");
                    return Result.Cancelled;
                }

                var region = FilledRegion.Create(doc, maskingRegionType.Id, activeView.Id,
                    new List<CurveLoop> { outerLoop });

                var boundaryStyle = FindLightingFixturesLineStyle(doc);
                if (boundaryStyle != null)
                    ApplyBoundaryLineStyle(doc, region, boundaryStyle);

                foreach (var fixture in fixtures)
                {
                    if (!fixtureToSymbol.TryGetValue(fixture.Id, out var stampSymbol))
                        continue;

                    if (!stampSymbol.IsActive)
                        stampSymbol.Activate();

                    var insertPoint = GetFixturePoint(fixture);
                    if (insertPoint == null) continue;

                    var stampInstance = doc.Create.NewFamilyInstance(insertPoint, stampSymbol, activeView);

                    double angle = GeometryHelper.GetTransformAngle(fixture.GetTotalTransform());
                    if (Math.Abs(angle) > 1e-9)
                    {
                        var axis = Line.CreateBound(insertPoint, insertPoint + XYZ.BasisZ * 10);
                        ElementTransformUtils.RotateElement(doc, stampInstance.Id, axis, angle);
                    }
                }

                tx.Commit();
            }

            if (failures.Count > 0)
            {
                TaskDialog.Show("TurboMask — Partial Success",
                    "The following fixture families were skipped:\n\n" +
                    string.Join("\n", failures));
            }

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("TurboMask Error", $"An unexpected error occurred:\n{ex.Message}");
            return Result.Failed;
        }
    }

    private static bool IsValidViewType(View view) =>
        view.ViewType == ViewType.FloorPlan ||
        view.ViewType == ViewType.CeilingPlan ||
        view.ViewType == ViewType.DraftingView;

    private static List<FamilyInstance> CollectLightingFixtures(IEnumerable<Element> elements)
    {
        var categoryId = new ElementId(BuiltInCategory.OST_LightingFixtures);
        var result = new List<FamilyInstance>();
        foreach (var element in elements)
        {
            if (element is FamilyInstance fi && fi.Category?.Id == categoryId)
                result.Add(fi);
        }
        return result;
    }

    private const string MaskRegionTypeName = "Masking Region";

    /// <summary>
    /// Returns a FilledRegionType named "Masking Region" with no foreground pattern, solid white
    /// background, and IsMasking=true. Reuses an existing one if present; otherwise duplicates any
    /// FilledRegionType in the project and configures the duplicate. Must be called inside an
    /// active Transaction.
    /// </summary>
    private static FilledRegionType? FindOrCreateMaskingRegionType(Document doc)
    {
        var existing = new FilteredElementCollector(doc)
            .OfClass(typeof(FilledRegionType))
            .Cast<FilledRegionType>()
            .FirstOrDefault(t => t.Name.Equals(MaskRegionTypeName, StringComparison.Ordinal));
        if (existing != null) return existing;

        var template = new FilteredElementCollector(doc)
            .OfClass(typeof(FilledRegionType))
            .Cast<FilledRegionType>()
            .FirstOrDefault();
        if (template == null) return null;

        if (template.Duplicate(MaskRegionTypeName) is not FilledRegionType newType)
            return null;

        var solidFillId = FindSolidFillPatternId(doc);
        var white = new Color(255, 255, 255);

        newType.IsMasking = true;
        newType.ForegroundPatternId = ElementId.InvalidElementId;
        if (solidFillId != ElementId.InvalidElementId)
            newType.BackgroundPatternId = solidFillId;
        newType.BackgroundPatternColor = white;
        newType.ForegroundPatternColor = white;

        return newType;
    }

    private static ElementId FindSolidFillPatternId(Document doc)
    {
        var drafting = new FilteredElementCollector(doc)
            .OfClass(typeof(FillPatternElement))
            .Cast<FillPatternElement>()
            .FirstOrDefault(p => p.GetFillPattern().IsSolidFill &&
                                 p.GetFillPattern().Target == FillPatternTarget.Drafting);
        return drafting?.Id ?? ElementId.InvalidElementId;
    }

    private const string MaskBoundaryLineStyleName = "Lighting Fixture";

    private static void ApplyBoundaryLineStyle(Document doc, FilledRegion region, GraphicsStyle style)
    {
        var sketchIds = region.GetDependentElements(new ElementClassFilter(typeof(Sketch)));
        if (sketchIds.Count == 0) return;
        if (doc.GetElement(sketchIds[0]) is not Sketch sketch) return;

        foreach (ElementId childId in sketch.GetAllElements())
        {
            if (doc.GetElement(childId) is CurveElement curveElement)
                curveElement.LineStyle = style;
        }
    }

    private static GraphicsStyle? FindLightingFixturesLineStyle(Document doc)
    {
        var linesCategory = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
        if (linesCategory == null) return null;

        foreach (Category sub in linesCategory.SubCategories)
        {
            if (sub.Name.Equals(MaskBoundaryLineStyleName, StringComparison.OrdinalIgnoreCase))
                return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
        }
        return null;
    }

    private static XYZ? GetFixturePoint(FamilyInstance fixture)
    {
        if (fixture.Location is LocationPoint lp)
            return lp.Point;
        if (fixture.Location is LocationCurve lc && lc.Curve != null)
            return lc.Curve.Evaluate(0.5, true);
        return null;
    }
}
