using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace TurboSuite.Zones.Services;

public static class LinkedRoomFinderService
{
    /// <summary>
    /// Returns the room name for a fixture's location, checking host document first,
    /// then all loaded linked documents. Returns null if no room is found.
    /// </summary>
    public static string? FindRoomName(Document hostDoc, FamilyInstance fixture)
    {
        Room? room = FindRoom(hostDoc, fixture);
        return room?.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
    }

    /// <summary>
    /// Returns the Room containing a fixture's location, checking host document first,
    /// then all loaded linked documents. Returns null if no room is found.
    /// </summary>
    public static Room? FindRoom(Document hostDoc, FamilyInstance fixture)
    {
        if (!TryGetFixturePoint(fixture, out XYZ hostPoint))
            return null;

        // 1. Check host document rooms first
        var hostRooms = new FilteredElementCollector(hostDoc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .OfClass(typeof(SpatialElement))
            .Cast<Room>();
        foreach (Room room in hostRooms)
            if (room.IsPointInRoom(hostPoint))
                return room;

        // 2. Check linked documents
        var linkInstances = new FilteredElementCollector(hostDoc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>();
        foreach (RevitLinkInstance link in linkInstances)
        {
            Document? linkDoc = link.GetLinkDocument();
            if (linkDoc == null) continue;
            Transform hostToLink = link.GetTotalTransform().Inverse;
            XYZ pointInLink = hostToLink.OfPoint(hostPoint);
            var rooms = new FilteredElementCollector(linkDoc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .OfClass(typeof(SpatialElement))
                .Cast<Room>();
            foreach (Room room in rooms)
                if (room.IsPointInRoom(pointInLink))
                    return room;
        }

        return null;
    }

    private static bool TryGetFixturePoint(FamilyInstance fixture, out XYZ point)
    {
        point = XYZ.Zero;
        if (fixture.Location is LocationPoint lp) { point = lp.Point; return true; }
        if (fixture.Location is LocationCurve lc && lc.Curve != null)
        {
            point = lc.Curve.Evaluate(0.5, true);
            return true;
        }
        return false;
    }
}
