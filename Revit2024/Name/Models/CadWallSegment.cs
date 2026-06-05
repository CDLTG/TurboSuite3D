using Autodesk.Revit.DB;

namespace TurboSuite.Name.Models;

/// <summary>
/// A single wall line segment in Revit coordinates (feet).
/// IsVirtual is true for gap-bridging segments not present in the original CAD file.
/// </summary>
public record CadWallSegment(XYZ StartPoint, XYZ EndPoint, bool IsVirtual = false);
