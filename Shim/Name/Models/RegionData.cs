using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace TurboSuite.Name.Models;

/// <summary>
/// A "Room Region" FilledRegion with its boundary loops for point-in-polygon testing.
/// </summary>
public record RegionData(ElementId RegionId, string ExistingComments, List<List<XYZ>> BoundaryLoops, bool IsFlagged = false);
