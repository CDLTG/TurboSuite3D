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

        List<FamilyInstance> ordered = new List<FamilyInstance>();
        HashSet<ElementId> remaining = new HashSet<ElementId>(fixtures.Select(f => f.Id));

        FamilyInstance current = fixtures[0];
        ordered.Add(current);
        remaining.Remove(current.Id);

        while (remaining.Count > 0)
        {
            XYZ? currentLocation = GeometryHelper.GetFixtureLocation(current);
            if (currentLocation == null) break;
            FamilyInstance? closest = null;
            double closestDistance = double.MaxValue;

            foreach (FamilyInstance fixture in fixtures)
            {
                if (!remaining.Contains(fixture.Id))
                    continue;

                XYZ? fixtureLocation = GeometryHelper.GetFixtureLocation(fixture);
                if (fixtureLocation == null) continue;
                double distance = currentLocation.DistanceTo(fixtureLocation);

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = fixture;
                }
            }

            if (closest != null)
            {
                ordered.Add(closest);
                remaining.Remove(closest.Id);
                current = closest;
            }
        }

        return ordered;
    }
}
