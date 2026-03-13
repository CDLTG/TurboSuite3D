using Autodesk.Revit.DB;

namespace TurboSuite.Name.Models;

/// <summary>
/// Room data extracted from a linked CAD file — name, ceiling height, and source location.
/// </summary>
public record CadRoomData(string RoomName, string CeilingHeight, XYZ RevitPoint);
