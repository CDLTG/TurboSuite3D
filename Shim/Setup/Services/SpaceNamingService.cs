using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using TurboSuite.Name;               // RoomNameNormalizer (shared with TurboName)
using TurboSuite.Shared.Services;    // LinkedRoomFinderService (BAND_ROOM), SpaceRoomFinderService

namespace TurboSuite.Setup.Services;

/// <summary>Outcome of a space-naming pass, for the summary dialog.</summary>
public sealed class SpaceNamingResult
{
    public int Total;
    public int Named;
    public int SkippedNamed;      // already had a name (only-blank mode)
    public int NoArchitectRoom;   // no architect Room resolved → left as-is
    public int NotWritable;       // name parameter was read-only
    public List<string> Preview = new();   // "<number>  <old> -> <new>" sample
}

/// <summary>
/// Seeds Space names from the architect's Rooms — the ONE place architect Rooms are still used (naming,
/// not runtime detection). For each Space, the architect Room it sits in is found via the shipped BAND_ROOM
/// finder (<see cref="LinkedRoomFinderService"/>) at the Space's plan location, and its name is normalized
/// (trim → strip '#' → UPPER, identical to TurboName) onto the Space's name.
///
/// Only-blank by default so manual disambiguation (LOWER POWDER / MAIN POWDER) is never clobbered; a force
/// pass re-pulls every Space (used deliberately when the architect renamed rooms).
/// </summary>
public static class SpaceNamingService
{
    public static SpaceNamingResult NameSpacesFromRooms(Document doc, bool force)
    {
        var result = new SpaceNamingResult();
        var roomCache = new LinkedRoomFinderService.RoomLookupCache(doc);

        List<Space> spaces = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_MEPSpaces)
            .WhereElementIsNotElementType()
            .Cast<Space>()
            .Where(s => s.Area > 0)
            .ToList();
        result.Total = spaces.Count;

        using (var t = new Transaction(doc, "Name Spaces from Architect Rooms"))
        {
            t.Start();
            foreach (Space space in spaces)
            {
                string current = SpaceRoomFinderService.ReadSpaceName(space);
                if (!force && !string.IsNullOrWhiteSpace(current))
                {
                    result.SkippedNamed++;
                    continue;
                }

                if (space.Location is not LocationPoint lp)
                {
                    result.NoArchitectRoom++;
                    continue;
                }

                // Probe at the Space's plan point, at a Z safely on this storey (its level + 1 ft), so
                // BAND_ROOM bands to the architect Room on the right floor regardless of LocationPoint.Z.
                double z = (space.Level?.ProjectElevation ?? lp.Point.Z) + 1.0;
                Room? room = roomCache.FindRoomAtPoint(new XYZ(lp.Point.X, lp.Point.Y, z));

                string raw = room?.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
                string normalized = RoomNameNormalizer.Normalize(raw);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    result.NoArchitectRoom++;
                    continue;
                }

                if (SpaceRoomFinderService.WriteSpaceName(space, normalized))
                {
                    result.Named++;
                    if (result.Preview.Count < 15)
                        result.Preview.Add($"{SpaceRoomFinderService.ReadSpaceNumber(space)}  {current} -> {normalized}");
                }
                else
                {
                    result.NotWritable++;
                }
            }
            t.Commit();
        }
        return result;
    }
}
