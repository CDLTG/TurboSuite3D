#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Driver.Models;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Driver.Services;

/// <summary>
/// Physically splits line-based fixtures in the Revit model to match
/// the algorithmic segment splits from DriverSelectionService.
/// Currently only supports work-plane-hosted (2D) fixtures.
/// Face-hosted (3D) fixtures are skipped — the Revit API does not support
/// creating line-based families on linked model faces with computed references.
/// </summary>
public class FixtureSplitService
{
    private readonly Document _doc;
    private readonly View _activeView;

    public FixtureSplitService(Document doc, View activeView)
    {
        _doc = doc;
        _activeView = activeView;
    }

    /// <summary>
    /// Result of a split operation, used by the caller to re-tag split fixtures.
    /// </summary>
    public class SplitResult
    {
        public ElementId LinearTagTypeId { get; set; } = ElementId.InvalidElementId;
        public List<ElementId> SplitFixtureIds { get; set; } = new();
    }

    public SplitResult SplitFixtures(List<SubDriverAssignment> assignments, ElectricalSystem circuit)
    {
        var result = new SplitResult();

        var segmentsByFixture = assignments
            .SelectMany(a => a.Segments)
            .Where(s => s.IsSplit)
            .GroupBy(s => s.FixtureId)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in segmentsByFixture)
        {
            var fixtureId = group.Key;
            var fixture = _doc.GetElement(fixtureId) as FamilyInstance;
            if (fixture == null) continue;

            if (!GeometryHelper.IsLineBasedFixture(fixture)) continue;

            // Skip face-hosted (3D) fixtures — auto-split only supports
            // work-plane-hosted fixtures. Face-hosted fixtures on linked model
            // faces cannot be recreated via the API without losing their host.
            if (fixture.HostFace != null) continue;

            // Capture tag type from the first fixture that has one
            if (result.LinearTagTypeId == ElementId.InvalidElementId)
                result.LinearTagTypeId = FindLinearLengthTagType(fixture.Id);

            var splitIds = SplitLineBasedFixture(fixture, group.ToList(), circuit);
            result.SplitFixtureIds.AddRange(splitIds);
        }

        return result;
    }

    private List<ElementId> SplitLineBasedFixture(
        FamilyInstance fixture,
        List<FixtureSegment> segments,
        ElectricalSystem circuit)
    {
        var locationCurve = (LocationCurve)fixture.Location;
        var originalCurve = locationCurve.Curve;
        var startPoint = originalCurve.GetEndPoint(0);
        var endPoint = originalCurve.GetEndPoint(1);
        var direction = (endPoint - startPoint).Normalize();
        double totalLength = startPoint.DistanceTo(endPoint);
        int segmentCount = segments.Count;
        double segmentLength = totalLength / segmentCount;

        segments.Sort((a, b) => string.Compare(a.SplitLabel, b.SplitLabel));

        // Create N copies at a large offset along the line direction.
        // Line-based families auto-join when endpoints are within 32mm.
        // Placing copies far away prevents them from inheriting join
        // relationships with adjacent fixtures, which would cause Revit
        // to revert the shortened curve back to full length on Regenerate.
        var copyOptions = new CopyPasteOptions();
        var allCopies = new List<(FamilyInstance instance, int index)>();
        var stagingOffset = direction * (totalLength * 10);
        var transform = Transform.CreateTranslation(stagingOffset);

        for (int i = 0; i < segmentCount; i++)
        {
            var copiedIds = ElementTransformUtils.CopyElements(
                _activeView,
                new List<ElementId> { fixture.Id },
                _activeView,
                transform,
                copyOptions);

            if (copiedIds == null || copiedIds.Count == 0) continue;

            FamilyInstance copy = null;
            foreach (var id in copiedIds)
            {
                if (_doc.GetElement(id) is FamilyInstance fi)
                {
                    copy = fi;
                    break;
                }
            }

            if (copy == null) continue;

            segments[i].FixtureId = copy.Id;
            allCopies.Add((copy, i));
        }

        // Add copies to circuit before deleting original to prevent empty circuit
        if (allCopies.Count > 0 && circuit != null)
        {
            var addSet = new ElementSet();
            foreach (var (copy, _) in allCopies)
                addSet.Insert(copy);
            circuit.AddToCircuit(addSet);
        }

        // Delete original and Regenerate to clear join relationships,
        // then reposition copies to correct segment locations.
        _doc.Delete(fixture.Id);
        _doc.Regenerate();

        foreach (var (instance, index) in allCopies)
        {
            if (instance.Location is not LocationCurve locCurve) continue;

            var segStart = startPoint + direction * (segmentLength * index);
            var segEnd = startPoint + direction * (segmentLength * (index + 1));
            locCurve.Curve = Line.CreateBound(segStart, segEnd);
        }

        return allCopies.Select(c => c.instance.Id).ToList();
    }

    private const string LinearTagFamilyName = "AL_Tag_Lighting Fixture (Linear Length)";

    /// <summary>
    /// Finds the tag type ID of the linear length tag on the given fixture in the active view.
    /// Returns InvalidElementId if no matching tag is found.
    /// </summary>
    private ElementId FindLinearLengthTagType(ElementId fixtureId)
    {
        var tags = new FilteredElementCollector(_doc, _activeView.Id)
            .OfClass(typeof(IndependentTag))
            .Cast<IndependentTag>();

        foreach (var tag in tags)
        {
            if (!tag.GetTaggedLocalElementIds().Contains(fixtureId))
                continue;

            ElementId typeId = tag.GetTypeId();
            if (typeId == ElementId.InvalidElementId) continue;

            if (_doc.GetElement(typeId) is FamilySymbol tagSymbol
                && string.Equals(tagSymbol.FamilyName, LinearTagFamilyName, StringComparison.OrdinalIgnoreCase))
            {
                return typeId;
            }
        }

        return ElementId.InvalidElementId;
    }
}
