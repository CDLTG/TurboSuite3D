using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Wire.Services;

internal static class FixtureOrderingService
{
    public static List<FamilyInstance> OrderFixturesByProximity(List<FamilyInstance> fixtures)
    {
        if (fixtures.Count <= 2)
            return fixtures;

        // Get locations for all fixtures
        var locations = new Dictionary<ElementId, XYZ>();
        foreach (FamilyInstance f in fixtures)
        {
            XYZ? loc = GeometryHelper.GetFixtureLocation(f);
            if (loc != null)
                locations[f.Id] = loc;
        }

        if (locations.Count < 2)
            return fixtures;

        // Find chain endpoints using double-farthest-point method
        var valid = fixtures.Where(f => locations.ContainsKey(f.Id)).ToList();

        FamilyInstance endpointA = valid[0];
        double maxDist = 0;
        foreach (FamilyInstance f in valid)
        {
            double dist = locations[valid[0].Id].DistanceTo(locations[f.Id]);
            if (dist > maxDist)
            {
                maxDist = dist;
                endpointA = f;
            }
        }

        FamilyInstance endpointB = endpointA;
        maxDist = 0;
        foreach (FamilyInstance f in valid)
        {
            double dist = locations[endpointA.Id].DistanceTo(locations[f.Id]);
            if (dist > maxDist)
            {
                maxDist = dist;
                endpointB = f;
            }
        }

        // Run nearest-neighbor from both endpoints, pick the shorter total path
        List<FamilyInstance> pathA = NearestNeighborFrom(endpointA, valid, locations);
        List<FamilyInstance> pathB = NearestNeighborFrom(endpointB, valid, locations);

        return TotalPathLength(pathA, locations) <= TotalPathLength(pathB, locations)
            ? pathA
            : pathB;
    }

    private static List<FamilyInstance> NearestNeighborFrom(
        FamilyInstance start, List<FamilyInstance> fixtures, Dictionary<ElementId, XYZ> locations)
    {
        List<FamilyInstance> ordered = new List<FamilyInstance>();
        HashSet<ElementId> remaining = new HashSet<ElementId>(fixtures.Select(f => f.Id));

        FamilyInstance current = start;
        ordered.Add(current);
        remaining.Remove(current.Id);

        while (remaining.Count > 0)
        {
            XYZ currentLoc = locations[current.Id];
            FamilyInstance? closest = null;
            double closestDist = double.MaxValue;

            foreach (FamilyInstance f in fixtures)
            {
                if (!remaining.Contains(f.Id)) continue;
                double dist = currentLoc.DistanceTo(locations[f.Id]);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = f;
                }
            }

            if (closest == null) break;
            ordered.Add(closest);
            remaining.Remove(closest.Id);
            current = closest;
        }

        return ordered;
    }

    private static double TotalPathLength(List<FamilyInstance> path, Dictionary<ElementId, XYZ> locations)
    {
        double total = 0;
        for (int i = 0; i < path.Count - 1; i++)
            total += locations[path[i].Id].DistanceTo(locations[path[i + 1].Id]);
        return total;
    }
}
