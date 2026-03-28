#nullable disable
using System;
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
        double northAngle = GetTextRotationAngle(doc);

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

                // No matching TextNote — find CAD data and place text notes
                var insideExisting = cadRoomData
                    .Where(cd => IsPointInZone(region.BoundaryLoops, cd.RevitPoint))
                    .ToList();

                var existingHeightEntries = insideExisting
                    .Where(cd => !string.IsNullOrEmpty(cd.CeilingHeight)).ToList();

                if (existingHeightEntries.Count == 1)
                {
                    // Single ceiling height — combine with room name at height's CAD location
                    var heightEntry = existingHeightEntries[0];
                    var (entryHeight, description) = CleanCeilingHeight(heightEntry.CeilingHeight);
                    string textContent = BuildTextContent(region.ExistingComments, entryHeight);
                    if (!string.IsNullOrEmpty(textContent))
                    {
                        var note = TextNote.Create(doc, view.Id, heightEntry.RevitPoint, textContent, textNoteTypeId);
                        note.HorizontalAlignment = HorizontalTextAlignment.Center;
                        note.VerticalAlignment = VerticalTextAlignment.Middle;
                        RotateToProjectNorth(doc, note, heightEntry.RevitPoint, northAngle);

                        if (!string.IsNullOrEmpty(description) && descriptionTextNoteTypeId != ElementId.InvalidElementId)
                        {
                            var descPoint = GetDescriptionPoint(heightEntry.RevitPoint, northAngle);
                            var descNote = TextNote.Create(doc, view.Id, descPoint, description, descriptionTextNoteTypeId);
                            descNote.HorizontalAlignment = HorizontalTextAlignment.Center;
                            descNote.VerticalAlignment = VerticalTextAlignment.Middle;
                            RotateToProjectNorth(doc, descNote, descPoint, northAngle);
                        }
                    }
                }
                else
                {
                    // 0 or multiple ceiling heights — place room name separately, then each height at its location
                    XYZ namePlacement = insideExisting.Count > 0
                        ? insideExisting.First().RevitPoint
                        : ComputeCentroid(region.BoundaryLoops[0]);

                    var nameNote = TextNote.Create(doc, view.Id, namePlacement, region.ExistingComments, textNoteTypeId);
                    nameNote.HorizontalAlignment = HorizontalTextAlignment.Center;
                    nameNote.VerticalAlignment = VerticalTextAlignment.Middle;
                    RotateToProjectNorth(doc, nameNote, namePlacement, northAngle);

                    foreach (var heightEntry in existingHeightEntries)
                    {
                        var (entryHeight, description) = CleanCeilingHeight(heightEntry.CeilingHeight);
                        if (string.IsNullOrEmpty(entryHeight)) continue;

                        var heightNote = TextNote.Create(doc, view.Id, heightEntry.RevitPoint, entryHeight, textNoteTypeId);
                        heightNote.HorizontalAlignment = HorizontalTextAlignment.Center;
                        heightNote.VerticalAlignment = VerticalTextAlignment.Middle;
                        RotateToProjectNorth(doc, heightNote, heightEntry.RevitPoint, northAngle);

                        if (!string.IsNullOrEmpty(description) && descriptionTextNoteTypeId != ElementId.InvalidElementId)
                        {
                            var descPoint = GetDescriptionPoint(heightEntry.RevitPoint, northAngle);
                            var descNote = TextNote.Create(doc, view.Id, descPoint, description, descriptionTextNoteTypeId);
                            descNote.HorizontalAlignment = HorizontalTextAlignment.Center;
                            descNote.VerticalAlignment = VerticalTextAlignment.Middle;
                            RotateToProjectNorth(doc, descNote, descPoint, northAngle);
                        }
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

            // Place a TextNote at each CAD entry location inside the region.
            // Name-only entries get just the room name; height-only entries get just the height.
            // If there's exactly 1 name and 1 height, combine them into a single text note
            // at the name entry's location.
            var nameEntries = inside.Where(cd => !string.IsNullOrEmpty(cd.RoomName)).ToList();
            var heightEntries = inside.Where(cd => !string.IsNullOrEmpty(cd.CeilingHeight)).ToList();

            if (nameEntries.Count == 1 && heightEntries.Count == 1)
            {
                // Single name + single height — combine at the name location
                var nameEntry = nameEntries[0];
                var (entryHeight, description) = CleanCeilingHeight(heightEntries[0].CeilingHeight);
                string textContent = BuildTextContent(nameEntry.RoomName, entryHeight);
                if (!string.IsNullOrEmpty(textContent))
                {
                    var note = TextNote.Create(doc, view.Id, nameEntry.RevitPoint, textContent, textNoteTypeId);
                    note.HorizontalAlignment = HorizontalTextAlignment.Center;
                    note.VerticalAlignment = VerticalTextAlignment.Middle;
                    RotateToProjectNorth(doc, note, nameEntry.RevitPoint, northAngle);

                    if (!string.IsNullOrEmpty(description) && descriptionTextNoteTypeId != ElementId.InvalidElementId)
                    {
                        var descPoint = GetDescriptionPoint(nameEntry.RevitPoint, northAngle);
                        var descNote = TextNote.Create(doc, view.Id, descPoint, description, descriptionTextNoteTypeId);
                        descNote.HorizontalAlignment = HorizontalTextAlignment.Center;
                        descNote.VerticalAlignment = VerticalTextAlignment.Middle;
                        RotateToProjectNorth(doc, descNote, descPoint, northAngle);
                    }
                }
            }
            else
            {
                // Place each entry independently at its own location
                foreach (var cadEntry in inside)
                {
                    var (entryHeight, description) = CleanCeilingHeight(cadEntry.CeilingHeight);
                    string textContent = BuildTextContent(cadEntry.RoomName, entryHeight);
                    if (string.IsNullOrEmpty(textContent)) continue;

                    var note = TextNote.Create(doc, view.Id, cadEntry.RevitPoint, textContent, textNoteTypeId);
                    note.HorizontalAlignment = HorizontalTextAlignment.Center;
                    note.VerticalAlignment = VerticalTextAlignment.Middle;
                    RotateToProjectNorth(doc, note, cadEntry.RevitPoint, northAngle);

                    if (!string.IsNullOrEmpty(description) && descriptionTextNoteTypeId != ElementId.InvalidElementId)
                    {
                        var descPoint = GetDescriptionPoint(cadEntry.RevitPoint, northAngle);
                        var descNote = TextNote.Create(doc, view.Id, descPoint, description, descriptionTextNoteTypeId);
                        descNote.HorizontalAlignment = HorizontalTextAlignment.Center;
                        descNote.VerticalAlignment = VerticalTextAlignment.Middle;
                        RotateToProjectNorth(doc, descNote, descPoint, northAngle);
                    }
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

        // Strip leading '+' (e.g., "+10'-0\"" → "10'-0\"")
        value = value.TrimStart('+');

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

    /// <summary>
    /// Returns the angle needed to rotate TextNotes so they align with model elements
    /// in a Project North-oriented view. ProjectPosition.Angle is the angle from True North
    /// to Project North, but elements in the view rotate by the negative of that angle.
    /// </summary>
    private static double GetTextRotationAngle(Document doc)
    {
        ProjectPosition pp = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
        return -pp.Angle;
    }

    /// <summary>
    /// Offsets a point "below" the anchor in the Project North coordinate frame,
    /// accounting for the rotation angle so the description stays beneath the main text.
    /// </summary>
    private static XYZ GetDescriptionPoint(XYZ anchor, double northAngle)
    {
        double dx = 0.5 * Math.Sin(northAngle);
        double dy = -0.5 * Math.Cos(northAngle);
        return new XYZ(anchor.X + dx, anchor.Y + dy, anchor.Z);
    }

    /// <summary>
    /// Rotates a TextNote to align with Project North if the angle is non-zero.
    /// </summary>
    private static void RotateToProjectNorth(Document doc, TextNote note, XYZ center, double northAngle)
    {
        if (Math.Abs(northAngle) < 1e-9) return;
        var axis = Line.CreateBound(center, center + XYZ.BasisZ);
        ElementTransformUtils.RotateElement(doc, note.Id, axis, northAngle);
    }
}
