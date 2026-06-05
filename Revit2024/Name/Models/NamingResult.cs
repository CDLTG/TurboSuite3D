using System.Collections.Generic;

namespace TurboSuite.Name.Models;

/// <summary>
/// Summary counts from a TurboName run.
/// </summary>
public record NamingResult(
    int Processed,
    int Skipped,
    int Ambiguous,
    int Unmatched,
    List<AmbiguousRegion> AmbiguousDetails,
    List<Autodesk.Revit.DB.ElementId> UnmatchedRegionIds);

/// <summary>
/// Details for one ambiguous region — the conflicting room names found inside it.
/// </summary>
public record AmbiguousRegion(Autodesk.Revit.DB.ElementId RegionId, List<string> Names);
