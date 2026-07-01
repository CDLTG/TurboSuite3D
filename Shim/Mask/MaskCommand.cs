using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Mask.Helpers;
using TurboSuite.Mask.Services;
using TurboSuite.Shared.Helpers;
using ElectricalWire = Autodesk.Revit.DB.Electrical.Wire;

namespace TurboSuite.Mask;

/// <summary>
/// TurboMask — places a project-level masking region under the selected elements and overlays a
/// view-level "stamp" (extracted from each fixture family's nested Generic Annotation) at every
/// selected fixture so the visible footprint graphics survive the mask.
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

            var fixtures = CollectFixtures(selectedElements);

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

            var boundsElements = selectedElements
                .Where(e => !IsOldStamp(e) && !IsOldMaskingRegion(e) && !IsTurboMaskGroup(e))
                .ToList();

            var outerLoop = SelectionBoundsService.BuildOuterLoop(boundsElements, activeView);
            if (outerLoop == null)
            {
                TaskDialog.Show("TurboMask", "Selected elements have no usable bounds in this view.");
                return Result.Cancelled;
            }

            using (var tx = new Transaction(doc, "TurboMask"))
            {
                tx.Start();

                var failureOptions = tx.GetFailureHandlingOptions();
                failureOptions.SetFailuresPreprocessor(new MaskFailurePreprocessor());
                tx.SetFailureHandlingOptions(failureOptions);

                UngroupSelectedTurboMaskGroups(doc, selectedElements);

                var maskingRegionType = FindOrCreateMaskingRegionType(doc);
                if (maskingRegionType == null)
                {
                    tx.RollBack();
                    TaskDialog.Show("TurboMask",
                        "Could not create a masking region type — the project has no FilledRegionType to duplicate.");
                    return Result.Cancelled;
                }

                DeleteExistingStamps(doc, activeView, fixtures);
                DeleteExistingMaskingRegions(doc, activeView, fixtures, maskingRegionType.Id);

                var region = FilledRegion.Create(doc, maskingRegionType.Id, activeView.Id,
                    new List<CurveLoop> { outerLoop });

                var boundaryStyle = FindLightingFixturesLineStyle(doc);
                if (boundaryStyle != null)
                    ApplyBoundaryLineStyle(doc, region, boundaryStyle);

                var groupMemberIds = new List<ElementId> { region.Id };

                foreach (var fixture in fixtures)
                {
                    if (!fixtureToSymbol.TryGetValue(fixture.Id, out var stampSymbol))
                        continue;

                    if (!stampSymbol.IsActive)
                        stampSymbol.Activate();

                    var insertPoint = GetFixturePoint(fixture);
                    if (insertPoint == null) continue;

                    var stampInstance = doc.Create.NewFamilyInstance(insertPoint, stampSymbol, activeView);
                    groupMemberIds.Add(stampInstance.Id);

                    double angle = GeometryHelper.GetTransformAngle(fixture.GetTotalTransform());
                    if (Math.Abs(angle) > 1e-9)
                    {
                        var axis = Line.CreateBound(insertPoint, insertPoint + XYZ.BasisZ * 10);
                        ElementTransformUtils.RotateElement(doc, stampInstance.Id, axis, angle);
                    }
                }

                var wireOverlayIds = DrawWireOverlays(doc, activeView, fixtures);
                groupMemberIds.AddRange(wireOverlayIds);

                RaiseTagsAboveStamps(doc, activeView, fixtures);

                if (groupMemberIds.Count > 1)
                {
                    var group = doc.Create.NewGroup(groupMemberIds);
                    group.GroupType.Name = NextTurboMaskGroupName(doc);
                }

                // Last creation wins the draw order — raise the user's own selected detail lines
                // above the mask (and the overlays) so they survive under the region.
                RaiseSelectedDetailLinesAboveMask(doc, activeView, selectedElements);

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

    private static readonly HashSet<ElementId> SupportedCategoryIds = new()
    {
        new ElementId(BuiltInCategory.OST_LightingFixtures),
        new ElementId(BuiltInCategory.OST_LightingDevices),
        new ElementId(BuiltInCategory.OST_ElectricalFixtures),
        new ElementId(BuiltInCategory.OST_ElectricalEquipment),
    };

    private const string TurboMaskGroupPrefix = "TurboMask";

    private static bool IsOldStamp(Element e) =>
        e is FamilyInstance fi && fi.Symbol?.Family?.Name?.StartsWith("Stamp_") == true;

    private static bool IsOldMaskingRegion(Element e) =>
        e is FilledRegion fr &&
        (e.Document.GetElement(fr.GetTypeId()) as FilledRegionType)?.Name == MaskRegionTypeName;

    private static bool IsTurboMaskGroup(Element e) =>
        e is Group g && g.GroupType?.Name?.StartsWith(TurboMaskGroupPrefix) == true;

    private static void UngroupSelectedTurboMaskGroups(Document doc, List<Element> selectedElements)
    {
        var typeIdsToDelete = new List<ElementId>();
        var memberStampIds = new List<ElementId>();
        var memberDetailIds = new List<ElementId>();

        foreach (var element in selectedElements)
        {
            if (element is Group group && group.GroupType?.Name?.StartsWith(TurboMaskGroupPrefix) == true)
            {
                typeIdsToDelete.Add(group.GroupType.Id);
                var memberIds = group.UngroupMembers();
                foreach (var id in memberIds)
                {
                    var member = doc.GetElement(id);
                    if (member is FamilyInstance fi
                        && fi.Symbol?.Family?.Name?.StartsWith("Stamp_") == true)
                        memberStampIds.Add(id);
                    else if (member is CurveElement ce && ce.ViewSpecific)
                        memberDetailIds.Add(id); // wire-overlay detail lines
                }
            }
        }

        if (memberStampIds.Count > 0)
            doc.Delete(memberStampIds);
        if (memberDetailIds.Count > 0)
            doc.Delete(memberDetailIds);
        if (typeIdsToDelete.Count > 0)
            doc.Delete(typeIdsToDelete);
    }

    private static string NextTurboMaskGroupName(Document doc)
    {
        var existingNames = new FilteredElementCollector(doc)
            .OfClass(typeof(GroupType))
            .Cast<GroupType>()
            .Where(gt => gt.Name.StartsWith(TurboMaskGroupPrefix))
            .Select(gt => gt.Name)
            .ToHashSet();

        int n = 1;
        while (existingNames.Contains($"{TurboMaskGroupPrefix} {n}"))
            n++;
        return $"{TurboMaskGroupPrefix} {n}";
    }

    private static List<FamilyInstance> CollectFixtures(IEnumerable<Element> elements)
    {
        var result = new List<FamilyInstance>();
        foreach (var element in elements)
        {
            if (element is FamilyInstance fi && fi.Category?.Id is ElementId catId
                && SupportedCategoryIds.Contains(catId))
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

    private static void DeleteExistingMaskingRegions(
        Document doc, View view, List<FamilyInstance> fixtures, ElementId maskingRegionTypeId)
    {
        var fixturePoints = new HashSet<XYZ>();
        foreach (var fi in fixtures)
        {
            var pt = GetFixturePoint(fi);
            if (pt != null)
                fixturePoints.Add(pt);
        }

        var regionIds = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(FilledRegion))
            .Cast<FilledRegion>()
            .Where(r => r.GetTypeId() == maskingRegionTypeId)
            .Where(r =>
            {
                var bbox = r.get_BoundingBox(view);
                if (bbox == null) return false;
                return fixturePoints.Any(pt =>
                    pt.X >= bbox.Min.X && pt.X <= bbox.Max.X &&
                    pt.Y >= bbox.Min.Y && pt.Y <= bbox.Max.Y);
            })
            .Select(r => r.Id)
            .ToList();

        if (regionIds.Count > 0)
            doc.Delete(regionIds);
    }

    private static void DeleteExistingStamps(Document doc, View view, List<FamilyInstance> fixtures)
    {
        var fixturePoints = new Dictionary<XYZ, ElementId>();
        foreach (var fi in fixtures)
        {
            var pt = GetFixturePoint(fi);
            if (pt != null)
                fixturePoints[pt] = fi.Id;
        }

        var stampIds = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(fi => fi.Symbol?.Family?.Name?.StartsWith("Stamp_") == true
                         && fi.Location is LocationPoint lp
                         && fixturePoints.Keys.Any(fp => fp.DistanceTo(lp.Point) < 0.01))
            .Select(fi => fi.Id)
            .ToList();

        if (stampIds.Count > 0)
            doc.Delete(stampIds);
    }

    /// <summary>
    /// Copy-in-place then delete originals — the copy lands at the top of the view's draw order,
    /// preventing stamps from hiding existing tags on the masked fixtures.
    /// </summary>
    private static void RaiseTagsAboveStamps(Document doc, View view, List<FamilyInstance> fixtures)
    {
        var fixtureIds = new HashSet<ElementId>(fixtures.Select(f => f.Id));

        var tagIds = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(IndependentTag))
            .Cast<IndependentTag>()
            .Where(tag => tag.GetTaggedLocalElementIds().Any(id => fixtureIds.Contains(id)))
            .Select(tag => tag.Id)
            .ToList();

        if (tagIds.Count == 0) return;

        var copyOpts = new CopyPasteOptions();
        ElementTransformUtils.CopyElements(view, tagIds, view, Transform.Identity, copyOpts);
        doc.Delete(tagIds);
    }

    /// <summary>
    /// Copy-in-place then delete the user's own selected view-specific detail lines so the copies
    /// land at the top of the view's draw order and stay visible over the masking region (draw
    /// order follows creation order). These are the user's linework — deliberately kept OUT of the
    /// TurboMask group so a later re-mask/ungroup (which deletes group member detail lines) never
    /// destroys them. Only the original selection is considered, so the freshly-drawn wire overlays
    /// and prior-group members (represented in the selection by their Group element, not their
    /// members) are never swept up.
    /// </summary>
    private static void RaiseSelectedDetailLinesAboveMask(
        Document doc, View view, List<Element> selectedElements)
    {
        var detailLineIds = selectedElements
            .OfType<CurveElement>()
            .Where(ce => ce.ViewSpecific && ce.OwnerViewId == view.Id)
            .Select(ce => ce.Id)
            .ToList();

        if (detailLineIds.Count == 0) return;

        var copyOpts = new CopyPasteOptions();
        ElementTransformUtils.CopyElements(view, detailLineIds, view, Transform.Identity, copyOpts);
        doc.Delete(detailLineIds);
    }

    /// <summary>
    /// Draws detail-line copies of every wire connected to the masked devices, on top of the
    /// masking region, so the still-connected (now hidden) wires stay visible. The real wires are
    /// never modified — these overlays are view-only stand-ins, like the fixture stamps, and join
    /// the TurboMask group. Returns the new detail-curve ids. Must run inside the active
    /// Transaction, after the masking region is created so the overlays draw on top.
    /// </summary>
    private static List<ElementId> DrawWireOverlays(Document doc, View view, List<FamilyInstance> devices)
    {
        var overlayIds = new List<ElementId>();

        var wireIds = new HashSet<ElementId>();
        foreach (var device in devices)
        {
            var connector = GeometryHelper.GetElectricalConnector(device);
            if (connector == null) continue;
            foreach (Connector connectedRef in connector.AllRefs)
            {
                if (connectedRef.Owner is ElectricalWire wire)
                    wireIds.Add(wire.Id);
            }
        }
        if (wireIds.Count == 0) return overlayIds;

        var wireStyle = FindWireLineStyle(doc);
        var options = new Options { View = view };

        foreach (var wireId in wireIds)
        {
            if (doc.GetElement(wireId) is not ElectricalWire wire) continue;

            // Mirror the wire's own in-view appearance: if a V/G filter restyles it (e.g. DMX
            // wires shown as Dot by Wire Type), stamp that same override onto the overlay so the
            // stand-in matches the real wire rather than falling back to plain "Wiring".
            var overrides = ResolveWireViewOverrides(doc, view, wire);

            var geometry = wire.get_Geometry(options);
            if (geometry == null) continue;

            foreach (GeometryObject go in geometry)
            {
                // Arc wires (e.g. fixture-to-fixture) come back as Curve objects; chamfer wires
                // (e.g. the driver-to-driver wires from TurboDriver) come back as a single
                // PolyLine. Handle both by emitting a detail curve per straight segment.
                switch (go)
                {
                    case Curve curve:
                        AddWireDetailCurve(doc, view, curve, wireStyle, overrides, overlayIds);
                        break;
                    case PolyLine polyLine:
                        var coords = polyLine.GetCoordinates();
                        for (int i = 1; i < coords.Count; i++)
                        {
                            if (coords[i - 1].DistanceTo(coords[i]) < doc.Application.ShortCurveTolerance)
                                continue;
                            AddWireDetailCurve(doc, view,
                                Line.CreateBound(coords[i - 1], coords[i]), wireStyle, overrides, overlayIds);
                        }
                        break;
                }
            }
        }
        return overlayIds;
    }

    private static void AddWireDetailCurve(
        Document doc, View view, Curve curve, GraphicsStyle? wireStyle,
        OverrideGraphicSettings? overrides, List<ElementId> overlayIds)
    {
        DetailCurve detail;
        try { detail = doc.Create.NewDetailCurve(view, curve); }
        catch { return; } // non-planar or degenerate segment — skip
        if (detail == null) return;

        if (wireStyle != null)
            detail.LineStyle = wireStyle;

        // Filter override sits on top of the "Wiring" line style, so the wire-type styling wins.
        if (overrides != null)
            view.SetElementOverrides(detail.Id, overrides);

        overlayIds.Add(detail.Id);
    }

    /// <summary>
    /// Returns the graphic override the active view applies to this wire via V/G filters, or null
    /// if no filter matches. Takes the first (highest-priority — filters resolve top-down) filter
    /// whose category set includes the wire's category and whose rules the wire passes; the whole
    /// override is reused so line pattern, weight, and color all carry over. When two filters set
    /// different properties on the same wire, only the top one is honored (per-property merging is
    /// deliberately not replicated).
    /// </summary>
    private static OverrideGraphicSettings? ResolveWireViewOverrides(Document doc, View view, ElectricalWire wire)
    {
        var wireCategoryId = wire.Category?.Id;
        if (wireCategoryId == null) return null;

        foreach (ElementId filterId in view.GetFilters())
        {
            switch (doc.GetElement(filterId))
            {
                case ParameterFilterElement pfe:
                    if (!pfe.GetCategories().Contains(wireCategoryId)) continue;
                    try
                    {
                        var elementFilter = pfe.GetElementFilter();
                        if (elementFilter == null || !elementFilter.PassesFilter(wire)) continue;
                    }
                    catch { continue; } // filter rule can't be evaluated against this wire — skip
                    return view.GetFilterOverrides(filterId);

                case SelectionFilterElement sfe:
                    if (!sfe.GetElementIds().Contains(wire.Id)) continue;
                    return view.GetFilterOverrides(filterId);
            }
        }
        return null;
    }

    private const string WireLineStyleName = "Wiring";

    /// <summary>
    /// Returns the "Wiring" Lines subcategory style (ships with the firm template) so the overlay
    /// detail lines resemble the real wires. Returns null when it doesn't exist, in which case the
    /// overlays use the view's default line style.
    /// </summary>
    private static GraphicsStyle? FindWireLineStyle(Document doc)
    {
        var linesCategory = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
        if (linesCategory == null) return null;

        foreach (Category sub in linesCategory.SubCategories)
        {
            if (sub.Name.Equals(WireLineStyleName, StringComparison.OrdinalIgnoreCase))
                return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
        }
        return null;
    }
}
