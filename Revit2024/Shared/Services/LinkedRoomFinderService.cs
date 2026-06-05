using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace TurboSuite.Shared.Services;

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

        return FindRoomAtPoint(hostDoc, hostPoint);
    }

    internal static bool TryGetFixturePoint(FamilyInstance fixture, out XYZ point)
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

    private static Room? FindRoomAtPoint(Document hostDoc, XYZ hostPoint)
    {
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

    /// <summary>
    /// Pre-collects all rooms and link transforms once for batch fixture lookups.
    /// Create once per command invocation, use for all fixtures, then discard.
    /// Optionally accepts a RegionRoomLookupService for 2D fallback when no Room is found.
    /// </summary>
    public class RoomLookupCache
    {
        private readonly List<Room> _hostRooms;
        private readonly List<(Transform HostToLink, List<Room> Rooms)> _linkedRooms;
        private readonly RegionRoomLookupService? _regionFallback;

        public RoomLookupCache(Document hostDoc, RegionRoomLookupService? regionFallback = null)
        {
            _regionFallback = regionFallback;

            _hostRooms = new FilteredElementCollector(hostDoc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .OfClass(typeof(SpatialElement))
                .Cast<Room>()
                .ToList();

            _linkedRooms = new List<(Transform, List<Room>)>();
            var linkInstances = new FilteredElementCollector(hostDoc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>();
            foreach (RevitLinkInstance link in linkInstances)
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                Transform hostToLink = link.GetTotalTransform().Inverse;
                var rooms = new FilteredElementCollector(linkDoc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .OfClass(typeof(SpatialElement))
                    .Cast<Room>()
                    .ToList();
                if (rooms.Count > 0)
                    _linkedRooms.Add((hostToLink, rooms));
            }
        }

        public Room? FindRoom(FamilyInstance fixture)
        {
            if (!TryGetFixturePoint(fixture, out XYZ hostPoint))
                return null;

            foreach (Room room in _hostRooms)
                if (room.IsPointInRoom(hostPoint))
                    return room;

            foreach (var (hostToLink, rooms) in _linkedRooms)
            {
                XYZ pointInLink = hostToLink.OfPoint(hostPoint);
                foreach (Room room in rooms)
                    if (room.IsPointInRoom(pointInLink))
                        return room;
            }

            return null;
        }

        /// <summary>
        /// Returns room name from Revit Room lookup, falling back to
        /// "Room Region" FilledRegion Comments if no Room is found.
        /// </summary>
        public string? FindRoomName(FamilyInstance fixture)
        {
            Room? room = FindRoom(fixture);
            if (room != null)
                return room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();

            // 2D fallback: check region Comments
            if (_regionFallback != null && TryGetFixturePoint(fixture, out XYZ point))
                return _regionFallback.FindRoomName(point);

            return null;
        }

        /// <summary>
        /// Returns the ElementId of the "Room Region" containing the fixture,
        /// or InvalidElementId if no region fallback is configured or no region matches.
        /// </summary>
        public ElementId FindRegionId(FamilyInstance fixture)
        {
            if (_regionFallback == null || !TryGetFixturePoint(fixture, out XYZ point))
                return ElementId.InvalidElementId;
            return _regionFallback.FindRegionId(point);
        }
    }
}
