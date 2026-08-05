using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace TurboSuite.Shared.Services;

/// <summary>
/// Resolves the architect Room containing a fixture, across the host document and every loaded link.
///
/// Runtime room detection no longer consults architect Rooms — it reads project-owned Spaces
/// (<see cref="SpaceRoomFinderService"/>). This finder survives as the seed for space naming
/// (<c>SpaceNamingService</c>) via <see cref="RoomLookupCache.FindRoomAtPoint"/>; keep the multi-source
/// link logic here since architect Rooms live in the linked arch model.
///
/// BAND_ROOM strategy: <see cref="Room.IsPointInRoom"/> is a 3D test, but architects leave room upper
/// limits at an arbitrary default, so ceiling-hosted and recessed fixtures sit above the room volume and
/// match nothing. So we neutralize height — probe each room at its own bounding-box mid-Z instead of the
/// fixture's real Z — which makes a fixture match every stacked storey in its plan column, then disambiguate
/// with <see cref="RoomBandSelector"/>: the plan match whose floor is highest at or below the fixture. Uses
/// only room bounding-box geometry; no levels, no <c>Elevation</c>/<c>ProjectElevation</c>, no host↔link
/// level correspondence.
///
/// Untested limitations (measured on one 3-storey model; each is a latent wrong-room / blank source, none
/// fixed here — Room Override is the escape hatch):
/// <list type="bullet">
/// <item>Phasing — rooms are collected across all phases with no phase filter, so on a renovation an
/// Existing and a New room sharing a footprint both become candidates.</item>
/// <item>Design options — no option filter; a room in a secondary option can shadow the primary.</item>
/// <item>Host-doc rooms shadowing link rooms — the host doc is collected first, so a stray room in the
/// electrical model would win for every fixture inside it.</item>
/// <item>Nested links — <see cref="RevitLinkInstance.GetLinkDocument"/> does not recurse; rooms in a link
/// inside the arch link are invisible.</item>
/// <item>Unloaded links — return null and are silently skipped; their rooms vanish.</item>
/// <item>Curve-based fixtures — assigned by curve midpoint only (see <see cref="TryGetFixturePoint"/>).</item>
/// </list>
/// </summary>
public static class LinkedRoomFinderService
{
    /// <summary>Plan prefilter half-width (ft). IsPointInRoom tests to wall centerlines, ~half a wall
    /// outside the finish-face bounding box, so the bbox prefilter must be widened by this or ~3% of real
    /// hits are silently dropped.</summary>
    private const double Margin = 0.5;

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

        return FindRoomAmongSources(BuildSources(hostDoc), hostPoint);
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

    // ── Shared cache structures ──────────────────────────────────────────────────────────────────────

    /// <summary>A room prepared for BAND_ROOM lookup: its plan extents and probe Z live in source
    /// coordinates (the point is transformed into source space to test); its floor lives in host
    /// coordinates so floors from different sources can be banded on one axis.</summary>
    private sealed class RoomEntry
    {
        public Room Room = null!;
        public double MinX, MaxX, MinY, MaxY;   // source coords — plan prefilter
        public double SrcMidZ;                   // source coords — height-neutralized probe Z
        public double HostFloorZ;                // host coords   — banding key (storey floor)
    }

    /// <summary>The host document (identity transform) or one link, with its rooms and the transform
    /// that takes a host point into this source's coordinates.</summary>
    private sealed class RoomSource
    {
        public Transform HostToSrc = Transform.Identity;
        public List<RoomEntry> Rooms = new();
    }

    private static List<RoomSource> BuildSources(Document hostDoc)
    {
        // Host first, so it wins the host-before-link tie in RoomBandSelector (equal-floor candidates
        // resolve to the earliest added).
        var sources = new List<RoomSource>
        {
            new RoomSource { HostToSrc = Transform.Identity, Rooms = CollectRooms(hostDoc, Transform.Identity) }
        };

        var linkInstances = new FilteredElementCollector(hostDoc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>();
        foreach (RevitLinkInstance link in linkInstances)
        {
            Document? linkDoc = link.GetLinkDocument();
            if (linkDoc == null) continue;                       // unloaded link — skipped (rooms vanish)
            Transform srcToHost = link.GetTotalTransform();
            var rooms = CollectRooms(linkDoc, srcToHost);
            if (rooms.Count > 0)
                sources.Add(new RoomSource { HostToSrc = srcToHost.Inverse, Rooms = rooms });
        }
        return sources;
    }

    private static List<RoomEntry> CollectRooms(Document doc, Transform srcToHost)
    {
        var entries = new List<RoomEntry>();
        var rooms = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .OfClass(typeof(SpatialElement))
            .Cast<Room>();
        foreach (Room room in rooms)
        {
            RoomEntry? entry = BuildEntry(room, srcToHost);
            if (entry != null)
                entries.Add(entry);
        }
        return entries;
    }

    private static RoomEntry? BuildEntry(Room room, Transform srcToHost)
    {
        if (room.Area <= 0) return null;                         // unplaced / unenclosed guard

        BoundingBoxXYZ? bb = null;
        try { bb = room.get_BoundingBox(null); } catch { /* fall through */ }
        if (bb == null) return null;

        // Floor Z in host coords: transform both box corners and take the lower.
        double hZ0 = srcToHost.OfPoint(bb.Min).Z;
        double hZ1 = srcToHost.OfPoint(bb.Max).Z;

        return new RoomEntry
        {
            Room = room,
            MinX = bb.Min.X, MaxX = bb.Max.X,
            MinY = bb.Min.Y, MaxY = bb.Max.Y,
            SrcMidZ = (bb.Min.Z + bb.Max.Z) / 2.0,
            HostFloorZ = Math.Min(hZ0, hZ1)
        };
    }

    private static Room? FindRoomAmongSources(List<RoomSource> sources, XYZ hostPoint)
    {
        var candidateRooms = new List<Room>();
        var candidateFloorZs = new List<double>();

        foreach (RoomSource src in sources)
        {
            XYZ p = src.HostToSrc.OfPoint(hostPoint);
            foreach (RoomEntry e in src.Rooms)
            {
                if (p.X < e.MinX - Margin || p.X > e.MaxX + Margin ||
                    p.Y < e.MinY - Margin || p.Y > e.MaxY + Margin)
                    continue;                                    // outside plan extents (+ centerline margin)

                if (e.Room.IsPointInRoom(new XYZ(p.X, p.Y, e.SrcMidZ)))
                {
                    candidateRooms.Add(e.Room);
                    candidateFloorZs.Add(e.HostFloorZ);
                }
            }
        }

        int idx = RoomBandSelector.SelectBandIndex(candidateFloorZs, hostPoint.Z);
        return idx >= 0 ? candidateRooms[idx] : null;
    }

    /// <summary>
    /// Pre-collects all rooms and link transforms once for batch fixture lookups.
    /// Create once per command invocation, use for all fixtures, then discard.
    /// Optionally accepts a RegionRoomLookupService for 2D fallback when no Room is found.
    /// </summary>
    public class RoomLookupCache
    {
        private readonly List<RoomSource> _sources;
        private readonly RegionRoomLookupService? _regionFallback;

        public RoomLookupCache(Document hostDoc, RegionRoomLookupService? regionFallback = null)
        {
            _regionFallback = regionFallback;
            _sources = BuildSources(hostDoc);
        }

        public Room? FindRoom(FamilyInstance fixture)
        {
            if (!TryGetFixturePoint(fixture, out XYZ hostPoint))
                return null;

            return FindRoomAmongSources(_sources, hostPoint);
        }

        /// <summary>
        /// Architect-Room lookup at an explicit host point — used by the space-naming command to seed a
        /// Space's name from the architect Room it sits in. Same BAND_ROOM logic as the fixture lookup;
        /// this is the ONE place architect Rooms are still consulted (naming, not runtime detection).
        /// </summary>
        public Room? FindRoomAtPoint(XYZ hostPoint) => FindRoomAmongSources(_sources, hostPoint);

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
    }
}
