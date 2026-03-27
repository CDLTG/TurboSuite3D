#nullable disable
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using TurboSuite.Name.Models;

namespace TurboSuite.Name.Services;

/// <summary>
/// Assigns CAD room names to filled regions and places TextNotes at CAD source locations.
/// Must be called inside an active Transaction.
/// </summary>
public static class RegionNamingService
{
    public static NamingResult AssignRoomNames(Document doc, View view,
        List<RegionData> regions, List<CadRoomData> cadRoomData,
        ElementId textNoteTypeId, ElementId descriptionTextNoteTypeId,
        ElementId roomRegionTypeId = null)
    {
        int processed = 0, skipped = 0, ambiguous = 0, unmatched = 0;
        var ambiguousDetails = new List<AmbiguousRegion>();
        var unmatchedRegionIds = new List<ElementId>();

        // Collect all TextNotes in the view for existing-comment checks
        var viewTextNotes = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(TextNote))
            .Cast<TextNote>()
            .ToList();

        foreach (var region in regions)
        {
            // Region already has Comments — check if a matching TextNote exists
            if (!string.IsNullOrWhiteSpace(region.ExistingComments))
            {
                bool hasMatchingTextNote = viewTextNotes.Any(tn =>
                    tn.Text.Contains(region.ExistingComments)
                    && IsPointInZone(region.BoundaryLoops, (tn.Coord)));

                if (hasMatchingTextNote)
                {
                    // Unflag if it was flagged and now has a matching text note
                    if (region.IsFlagged && roomRegionTypeId != null)
                        doc.GetElement(region.RegionId)?.ChangeTypeId(roomRegionTypeId);
                    skipped++;
                    continue;
                }

                // No matching TextNote — create one using CAD location or centroid fallback
                var insideExisting = cadRoomData
                    .Where(cd => IsPointInZone(region.BoundaryLoops, cd.RevitPoint))
                    .ToList();

                XYZ placementPoint;
                string existingHeight = "";
                string existingDesc = "";

                if (insideExisting.Count > 0)
                {
                    var cadEntry = insideExisting.First();
                    placementPoint = cadEntry.RevitPoint;
                    (existingHeight, existingDesc) = CleanCeilingHeight(cadEntry.CeilingHeight);
                }
                else
                {
                    // No CAD data in region — fall back to centroid
                    placementPoint = ComputeCentroid(region.BoundaryLoops[0]);
                }

                string textContent = BuildTextContent(region.ExistingComments, existingHeight);
                if (!string.IsNullOrEmpty(textContent))
                {
                    var note = TextNote.Create(doc, view.Id, placementPoint, textContent, textNoteTypeId);
                    note.HorizontalAlignment = HorizontalTextAlignment.Center;
                    note.VerticalAlignment = VerticalTextAlignment.Middle;

                    if (!string.IsNullOrEmpty(existingDesc) && descriptionTextNoteTypeId != ElementId.InvalidElementId)
                    {
                        var descPoint = new XYZ(placementPoint.X, placementPoint.Y - 0.5, placementPoint.Z);
                        var descNote = TextNote.Create(doc, view.Id, descPoint, existingDesc, descriptionTextNoteTypeId);
                        descNote.HorizontalAlignment = HorizontalTextAlignment.Center;
                        descNote.VerticalAlignment = VerticalTextAlignment.Middle;
                    }
                }

                // Unflag if it was flagged and we just placed a text note
                if (region.IsFlagged && roomRegionTypeId != null)
                    doc.GetElement(region.RegionId)?.ChangeTypeId(roomRegionTypeId);

                processed++;
                continue;
            }

            // Find all CAD room data points inside this region
            var inside = cadRoomData.Where(cd => IsPointInZone(region.BoundaryLoops, cd.RevitPoint)).ToList();

            if (inside.Count == 0)
            {
                unmatched++;
                unmatchedRegionIds.Add(region.RegionId);
                continue;
            }

            // Check for ambiguous room names (distinct non-empty names)
            var distinctNames = inside
                .Select(cd => cd.RoomName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .ToList();

            if (distinctNames.Count > 1)
            {
                ambiguous++;
                ambiguousDetails.Add(new AmbiguousRegion(region.RegionId, distinctNames));
                continue;
            }

            // Use first match for room name and ceiling height
            string roomName = distinctNames.FirstOrDefault() ?? "";
            string ceilingHeight = inside
                .Select(cd => cd.CeilingHeight)
                .FirstOrDefault(ch => !string.IsNullOrEmpty(ch)) ?? "";

            // Write room name to Comments (only if non-empty)
            if (!string.IsNullOrEmpty(roomName))
            {
                var element = doc.GetElement(region.RegionId);
                element?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                    ?.Set(roomName);
            }

            // Place a TextNote at each CAD block location inside the region
            foreach (var cadEntry in inside)
            {
                var (entryHeight, description) = CleanCeilingHeight(cadEntry.CeilingHeight);
                string textContent = BuildTextContent(cadEntry.RoomName, entryHeight);
                if (string.IsNullOrEmpty(textContent)) continue;

                var note = TextNote.Create(doc, view.Id, cadEntry.RevitPoint, textContent, textNoteTypeId);
                note.HorizontalAlignment = HorizontalTextAlignment.Center;
                note.VerticalAlignment = VerticalTextAlignment.Middle;

                // Place ceiling description as a separate, smaller TextNote below
                if (!string.IsNullOrEmpty(description) && descriptionTextNoteTypeId != ElementId.InvalidElementId)
                {
                    var descPoint = new XYZ(cadEntry.RevitPoint.X, cadEntry.RevitPoint.Y - 0.5, cadEntry.RevitPoint.Z);
                    var descNote = TextNote.Create(doc, view.Id, descPoint, description, descriptionTextNoteTypeId);
                    descNote.HorizontalAlignment = HorizontalTextAlignment.Center;
                    descNote.VerticalAlignment = VerticalTextAlignment.Middle;
                }
            }

            // Unflag if it was flagged and we just assigned a name
            if (region.IsFlagged && roomRegionTypeId != null)
                doc.GetElement(region.RegionId)?.ChangeTypeId(roomRegionTypeId);

            processed++;
        }

        return new NamingResult(processed, skipped, ambiguous, unmatched, ambiguousDetails, unmatchedRegionIds);
    }

    private static readonly string[] PreservedCeilingWords =
    {
        "Vault", "Slope", "Barrel", "Tray", "Tin",
        "Suspend", "Drop", "Cathedral", "Coffer", "Dome", "Groin"
    };

    /// <summary>
    /// Strips alphabetical characters, spaces, and periods from ceiling height values.
    /// Returns the cleaned numeric height and any preserved ceiling description keywords separately.
    /// E.g., "10' - 0\" CLG." → ("10'-0\"", "")
    /// E.g., "10' - 0\" Vaulted" → ("10'-0\"", "VAULTED")
    /// </summary>
    private static (string Height, string Description) CleanCeilingHeight(string value)
    {
        if (string.IsNullOrEmpty(value)) return (value, "");

        // Extract words that match preserved keywords (case-insensitive substring match)
        var words = Regex.Matches(value, @"[a-zA-Z]+")
            .Cast<Match>()
            .Select(m => m.Value)
            .Where(w => PreservedCeilingWords.Any(k =>
                w.IndexOf(k, System.StringComparison.OrdinalIgnoreCase) >= 0))
            .ToList();

        // Strip all alpha, periods, spaces
        string cleaned = Regex.Replace(value, @"[a-zA-Z.\s]", "");

        string description = words.Count > 0
            ? string.Join(" ", words).ToUpper()
            : "";

        return (cleaned, description);
    }

    private static string BuildTextContent(string roomName, string ceilingHeight)
    {
        if (!string.IsNullOrEmpty(roomName) && !string.IsNullOrEmpty(ceilingHeight))
            return $"{roomName}\n{ceilingHeight}";
        if (!string.IsNullOrEmpty(roomName))
            return roomName;
        if (!string.IsNullOrEmpty(ceilingHeight))
            return ceilingHeight;
        return "";
    }

    private static XYZ ComputeCentroid(List<XYZ> outerLoop)
    {
        double x = 0, y = 0, z = 0;
        foreach (var pt in outerLoop)
        {
            x += pt.X;
            y += pt.Y;
            z += pt.Z;
        }
        int n = outerLoop.Count;
        return new XYZ(x / n, y / n, z / n);
    }

    private static bool IsPointInZone(List<List<XYZ>> loops, XYZ point)
    {
        bool hit = IsPointInPolygon2D(loops[0], point);
        if (hit && loops.Count > 1)
        {
            for (int i = 1; i < loops.Count; i++)
            {
                if (IsPointInPolygon2D(loops[i], point))
                    return false;
            }
        }
        return hit;
    }

    private static bool IsPointInPolygon2D(List<XYZ> polygon, XYZ point)
    {
        if (polygon == null || polygon.Count < 3) return false;

        double px = point.X;
        double py = point.Y;
        bool inside = false;

        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            double xi = polygon[i].X, yi = polygon[i].Y;
            double xj = polygon[j].X, yj = polygon[j].Y;

            if (((yi > py) != (yj > py)) &&
                (px < (xj - xi) * (py - yi) / (yj - yi) + xi))
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
