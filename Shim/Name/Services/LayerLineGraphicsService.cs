#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Name.Services;

/// <summary>
/// One entry in the Line Graphics flyout's Pattern dropdown: an <see cref="ElementId"/> paired with its display
/// name. Two synthetic entries bookend the project's named <see cref="LinePatternElement"/>s — "&lt;No
/// Override&gt;" (<see cref="ElementId.InvalidElementId"/>) and "Solid" (<see
/// cref="LinePatternElement.GetSolidPatternId"/>, id -3000010, which is NOT a collectible element).
/// </summary>
public sealed record LinePatternOption(ElementId Id, string Name);

/// <summary>
/// Read-side helper for the per-layer Lines override (TurboName-12). Builds the Pattern dropdown roster in a
/// valid API context; the flyout composes and clears the actual <see cref="OverrideGraphicSettings"/> itself
/// (pure value-object work, no transaction — spike-confirmed), and the handler writes it via
/// <see cref="View.SetCategoryOverrides"/>.
/// </summary>
public static class LayerLineGraphicsService
{
    /// <summary>Sentinel id for the "&lt;No Override&gt;" pattern choice (clears the line pattern override).</summary>
    public static ElementId NoOverridePatternId => ElementId.InvalidElementId;

    /// <summary>
    /// The Pattern dropdown roster: "&lt;No Override&gt;", "Solid", then every project <see
    /// cref="LinePatternElement"/> naturally sorted by name. Call from a valid API context.
    /// </summary>
    public static List<LinePatternOption> GetPatternOptions(Document doc)
    {
        var options = new List<LinePatternOption>
        {
            new(NoOverridePatternId, "<No Override>"),
            new(LinePatternElement.GetSolidPatternId(), "Solid"),
        };

        if (doc == null) return options;

        var natural = new NaturalStringComparer();
        var named = new FilteredElementCollector(doc)
            .OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>()
            .Where(p => !string.IsNullOrEmpty(p.Name))
            .OrderBy(p => p.Name, natural)
            .Select(p => new LinePatternOption(p.Id, p.Name));

        options.AddRange(named);
        return options;
    }

    /// <summary>
    /// Per-pattern on/off segment lengths (in feet, dash-first, alternating, even-length) for the layer-table
    /// line-preview swatches — the raw shape a row scales into a WPF dash array against its own weight. Solid
    /// and "&lt;No Override&gt;" aren't keyed (they render as a plain solid line). Call from a valid API context.
    /// </summary>
    public static Dictionary<ElementId, double[]> GetPatternDashArrays(Document doc)
    {
        var map = new Dictionary<ElementId, double[]>();
        if (doc == null) return map;

        foreach (var pe in new FilteredElementCollector(doc)
                     .OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>())
        {
            try
            {
                var segs = pe.GetLinePattern()?.GetSegments();
                if (segs == null || segs.Count == 0) continue;
                var arr = BuildOnOffFeet(segs);
                if (arr.Length >= 2) map[pe.Id] = arr;
            }
            catch { /* skip an unreadable pattern — its swatch falls back to solid */ }
        }
        return map;
    }

    // Fold a pattern's segments into an alternating [on, off, on, off, …] feet array, dash-first, even length.
    // Dash/Dot → on, Space → off. Real Revit patterns already arrive clean (dash-first, alternating); the merge/
    // seed branches only guard malformed input so we never desync the on/off phase.
    private static double[] BuildOnOffFeet(IList<LinePatternSegment> segs)
    {
        var list = new List<double>();
        bool wantOn = true;
        foreach (var s in segs)
        {
            bool isOn = s.Type != LinePatternSegmentType.Space;
            double len = Math.Max(0.0, s.Length);
            if (isOn == wantOn)
            {
                list.Add(len);
                wantOn = !wantOn;
            }
            else if (list.Count == 0)
            {
                list.Add(0.0);            // pattern opened with a space — seed an empty dash so we stay dash-first
                list.Add(len);
                wantOn = true;
            }
            else
            {
                list[list.Count - 1] += len; // two same-kind in a row — merge into the current slot
            }
        }
        if (list.Count % 2 != 0) list.Add(0.0); // trailing gap so the dash array repeats cleanly
        return list.ToArray();
    }
}
