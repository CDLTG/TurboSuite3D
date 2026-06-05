using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Tag.Constants;

namespace TurboSuite.Tag.Services;

internal sealed class LinearRun
{
    public List<FamilyInstance> Members { get; }
    public FamilyInstance Lead { get; }

    public LinearRun(List<FamilyInstance> orderedMembers)
    {
        Members = orderedMembers;
        // Cheapest tie-break: lower-index of the two middle entries on even counts.
        Lead = orderedMembers[(orderedMembers.Count - 1) / 2];
    }
}

internal static class LinearRunService
{
    public static List<LinearRun> BuildRuns(IEnumerable<FamilyInstance> fixtures)
    {
        var endpoints = new Dictionary<ElementId, (XYZ Start, XYZ End)>();
        var withCurves = new List<FamilyInstance>();

        foreach (FamilyInstance f in fixtures)
        {
            if (f.Location is LocationCurve lc && lc.Curve != null)
            {
                endpoints[f.Id] = (lc.Curve.GetEndPoint(0), lc.Curve.GetEndPoint(1));
                withCurves.Add(f);
            }
        }

        // Adjacency: fixtures share an endpoint within tolerance.
        var adjacency = new Dictionary<ElementId, List<ElementId>>();
        foreach (var f in withCurves)
            adjacency[f.Id] = new List<ElementId>();

        double tol = TagConstants.LinearContinuityToleranceFeet;
        for (int i = 0; i < withCurves.Count; i++)
        {
            var a = endpoints[withCurves[i].Id];
            for (int j = i + 1; j < withCurves.Count; j++)
            {
                var b = endpoints[withCurves[j].Id];
                if (a.Start.DistanceTo(b.Start) < tol ||
                    a.Start.DistanceTo(b.End) < tol ||
                    a.End.DistanceTo(b.Start) < tol ||
                    a.End.DistanceTo(b.End) < tol)
                {
                    adjacency[withCurves[i].Id].Add(withCurves[j].Id);
                    adjacency[withCurves[j].Id].Add(withCurves[i].Id);
                }
            }
        }

        // Connected components via BFS, then order each component along the chain.
        var byId = withCurves.ToDictionary(f => f.Id, f => f);
        var visited = new HashSet<ElementId>();
        var runs = new List<LinearRun>();

        foreach (var f in withCurves)
        {
            if (visited.Contains(f.Id))
                continue;

            var component = new List<ElementId>();
            var queue = new Queue<ElementId>();
            queue.Enqueue(f.Id);
            visited.Add(f.Id);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                component.Add(cur);
                foreach (var n in adjacency[cur])
                {
                    if (visited.Add(n))
                        queue.Enqueue(n);
                }
            }

            List<FamilyInstance> ordered = OrderChain(component, adjacency, byId);
            runs.Add(new LinearRun(ordered));
        }

        return runs;
    }

    private static List<FamilyInstance> OrderChain(
        List<ElementId> component,
        Dictionary<ElementId, List<ElementId>> adjacency,
        Dictionary<ElementId, FamilyInstance> byId)
    {
        if (component.Count == 1)
            return new List<FamilyInstance> { byId[component[0]] };

        // Start from an endpoint (degree 1). If none (cycle/branching), start anywhere.
        ElementId startId = component.FirstOrDefault(id => adjacency[id].Count == 1)
                            ?? component[0];

        var ordered = new List<FamilyInstance>(component.Count);
        var seen = new HashSet<ElementId>();
        ElementId? current = startId;
        ElementId? prev = null;

        while (current != null && seen.Add(current))
        {
            ordered.Add(byId[current]);
            var nexts = adjacency[current].Where(n => n != prev && !seen.Contains(n)).ToList();
            prev = current;
            current = nexts.Count > 0 ? nexts[0] : null;
        }

        // If branching prevented walking all members, append the rest in arbitrary order.
        foreach (var id in component)
        {
            if (!seen.Contains(id))
                ordered.Add(byId[id]);
        }

        return ordered;
    }
}
