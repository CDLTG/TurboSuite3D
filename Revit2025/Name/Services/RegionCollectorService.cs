#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Name.Models;

namespace TurboSuite.Name.Services;

/// <summary>
/// Collects "Room Region" type FilledRegions from the active view.
/// </summary>
public static class RegionCollectorService
{
    private const string RoomRegionTypeName = "Room Region";
    private const string FlaggedRegionTypeName = "Room Region (Flagged)";
    private const string EmptyRegionTypeName = "Room Region (Empty)";

    public static List<RegionData> CollectRegions(Document doc, View view)
    {
        var regions = new List<RegionData>();
        var allRegions = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(FilledRegion))
            .Cast<FilledRegion>()
            .ToList();

        foreach (var region in allRegions)
        {
            if (region.IsMasking) continue;

            var typeId = region.GetTypeId();
            if (typeId == ElementId.InvalidElementId) continue;
            var regionType = doc.GetElement(typeId);
            if (regionType == null) continue;

            string typeName = regionType.Name;
            bool isFlagged = typeName == FlaggedRegionTypeName || typeName == EmptyRegionTypeName;
            if (typeName != RoomRegionTypeName && !isFlagged) continue;

            string comments = region.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                ?.AsString() ?? "";

            var boundaries = region.GetBoundaries();
            if (boundaries == null || boundaries.Count == 0) continue;

            var loopPoints = new List<List<XYZ>>();
            foreach (var loop in boundaries)
            {
                var points = new List<XYZ>();
                foreach (var curve in loop)
                {
                    var tessellated = curve.Tessellate();
                    for (int i = 0; i < tessellated.Count - 1; i++)
                        points.Add(tessellated[i]);
                }
                loopPoints.Add(points);
            }

            regions.Add(new RegionData(region.Id, comments, loopPoints, isFlagged));
        }

        return regions;
    }
}
