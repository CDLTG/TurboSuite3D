#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Name.Models;
using TurboSuite.Name.Regions;
using TurboSuite.Shared.Helpers;

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

        // A TextNote's on-screen angle is view-relative: rotation 0 renders horizontal no matter
        // how the crop is rotated. So in a rotated crop the labels read horizontal instead of
        // running with the model. To make them align to the model (parallel to the walls/rooms
        // they name) we counter-rotate every note by -cropAngle — EXCEPT at square crop rotations
        // (0/90/180/270), where we snap upright so labels never render sideways or upside-down
        // (same rule as TurboDriver's stack). `totalTilt` folds this together with the existing
        // Project-North compensation and drives BOTH the note rotation and the description offset,
        // so the description stays directly beneath the (now-tilted) label. Identity at crop 0°
        // (textCropRotation = 0, ScreenOffsetToModel = identity) — un-rotated production views are
        // byte-for-byte unaffected.
        double cropAngle = ViewOrientationHelper.GetViewRotation(view);
        double textCropRotation = ViewOrientationHelper.IsNearRightAngle(cropAngle) ? 0.0 : -cropAngle;
        double totalTilt = northAngle + textCropRotation;

        // Collect all TextNotes in the view for existing-comment checks
        var viewTextNotes = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(TextNote))
            .Cast<TextNote>()
            .ToList();

        // Create a styled, tilt-rotated note and register it into viewTextNotes so later dedup checks in
        // THIS run see it — the list starts as the pre-run snapshot and grows as notes are placed, so two
        // CAD entries (or two regions) resolving to identical in-zone text no longer each place a duplicate.
        TextNote PlaceNote(ElementId typeId, XYZ point, string content)
        {
            var note = TextNote.Create(doc, view.Id, point, content, typeId);
            note.HorizontalAlignment = HorizontalTextAlignment.Center;
            note.VerticalAlignment = VerticalTextAlignment.Middle;
            RotateNote(doc, note, point, totalTilt);
            viewTextNotes.Add(note);
            return note;
        }

        foreach (var region in regions)
        {
            // Region already has Comments — check if a matching TextNote exists
            if (!string.IsNullOrWhiteSpace(region.ExistingComments))
            {
                bool hasMatchingTextNote = viewTextNotes.Any(tn =>
                    NoteMatchesContent(tn.Text, region.ExistingComments)
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
                        PlaceNote(textNoteTypeId, heightEntry.RevitPoint, textContent);

                        if (!string.IsNullOrEmpty(description) && descriptionTextNoteTypeId != ElementId.InvalidElementId)
                        {
                            var descPoint = GetDescriptionPoint(view, heightEntry.RevitPoint, totalTilt);
                            PlaceNote(descriptionTextNoteTypeId, descPoint, description);
                        }
                    }
                }
                else
                {
                    // 0 or multiple ceiling heights — place room name separately, then each height at its location
                    XYZ namePlacement = insideExisting.Count > 0
                        ? insideExisting.First().RevitPoint
                        : ComputeCentroid(region.BoundaryLoops[0]);

                    PlaceNote(textNoteTypeId, namePlacement, region.ExistingComments);

                    foreach (var heightEntry in existingHeightEntries)
                    {
                        var (entryHeight, description) = CleanCeilingHeight(heightEntry.CeilingHeight);
                        if (string.IsNullOrEmpty(entryHeight)) continue;

                        PlaceNote(textNoteTypeId, heightEntry.RevitPoint, entryHeight);

                        if (!string.IsNullOrEmpty(description) && descriptionTextNoteTypeId != ElementId.InvalidElementId)
                        {
                            var descPoint = GetDescriptionPoint(view, heightEntry.RevitPoint, totalTilt);
                            PlaceNote(descriptionTextNoteTypeId, descPoint, description);
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
                if (!string.IsNullOrEmpty(textContent)
                    && !HasMatchingTextNote(viewTextNotes, region.BoundaryLoops, nameEntry.RevitPoint, textContent))
                {
                    PlaceNote(textNoteTypeId, nameEntry.RevitPoint, textContent);
                }

                if (!string.IsNullOrEmpty(description) && descriptionTextNoteTypeId != ElementId.InvalidElementId)
                {
                    var descPoint = GetDescriptionPoint(view, nameEntries[0].RevitPoint, totalTilt);
                    if (!HasMatchingTextNote(viewTextNotes, region.BoundaryLoops, descPoint, description))
                        PlaceNote(descriptionTextNoteTypeId, descPoint, description);
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

                    if (!HasMatchingTextNote(viewTextNotes, region.BoundaryLoops, cadEntry.RevitPoint, textContent))
                    {
                        PlaceNote(textNoteTypeId, cadEntry.RevitPoint, textContent);
                    }

                    if (!string.IsNullOrEmpty(description) && descriptionTextNoteTypeId != ElementId.InvalidElementId)
                    {
                        var descPoint = GetDescriptionPoint(view, cadEntry.RevitPoint, totalTilt);
                        if (!HasMatchingTextNote(viewTextNotes, region.BoundaryLoops, descPoint, description))
                            PlaceNote(descriptionTextNoteTypeId, descPoint, description);
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

    // Numeric parse + round-to-nearest-inch + descriptor split now lives in the Revit-free Core (unit-tested).
    // E.g. "10' - 0\" CLG." → ("10'-0\"", ""); "10'-6 1/2\" Vaulted" → ("10'-7\"", "VAULTED").
    private static (string Height, string Description) CleanCeilingHeight(string value)
        => CeilingHeightFormatter.Clean(value);

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

    // A same-text note within this of the target point is the SAME stamp (a re-run, or a doubled-up CAD
    // entry at one spot) — not a distinct marker elsewhere in the room. Kept tight: architects place
    // repeated callouts (e.g. the same ceiling height on the left and right of a room) feet apart, so this
    // only ever collapses genuinely co-located notes, never two real markers of the same value.
    private const double DuplicateNoteTolerance = 0.5; // ft

    /// <summary>
    /// Returns true if any TextNote in the view matches <paramref name="text"/> (whole-line), sits inside
    /// the region boundary, AND is within <see cref="DuplicateNoteTolerance"/> of <paramref name="point"/>.
    /// The point gate is what lets a room carry the same value at multiple locations (three "10'-0"" ceiling
    /// callouts) while still suppressing a true duplicate placed at the same spot.
    /// </summary>
    private static bool HasMatchingTextNote(List<TextNote> viewTextNotes,
        List<List<XYZ>> boundaryLoops, XYZ point, string text)
    {
        return viewTextNotes.Any(tn =>
            NoteMatchesContent(tn.Text, text)
            && tn.Coord.DistanceTo(point) < DuplicateNoteTolerance
            && IsPointInZone(boundaryLoops, tn.Coord));
    }

    /// <summary>
    /// True if every non-empty line of <paramref name="content"/> appears as a whole line in
    /// <paramref name="noteText"/> (trimmed, case-insensitive). Line-aware on purpose: a plain
    /// substring test lets a short name match a longer note ("BED" inside a "BEDROOM 2" note),
    /// wrongly treating the region as already-labeled; whole-line matching still tolerates a
    /// multi-line note (name + ceiling height) carrying extra lines.
    /// </summary>
    /// <remarks>
    /// Split on BOTH '\r' and '\n'. Revit's <see cref="TextNote.Text"/> separates lines with a bare
    /// carriage return ('\r'), NOT the '\n' our <see cref="BuildTextContent"/> writes — so a two-line
    /// "NAME\rHEIGHT\r" note split on '\n' alone stays one chunk and its internal '\r' survives the Trim,
    /// so the name line never matches. That made every combined name+height note miss the re-run skip test
    /// and get re-stamped at the seed on every run (single-line notes escaped it only because Trim eats a
    /// lone trailing '\r'). Splitting on both characters is the whole fix.
    /// </remarks>
    private static bool NoteMatchesContent(string noteText, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;

        var noteLines = new HashSet<string>(
            (noteText ?? "").Split('\r', '\n').Select(l => l.Trim()).Where(l => l.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        foreach (var line in content.Split('\r', '\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            if (!noteLines.Contains(line)) return false;
        return true;
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
    /// in a Project North-oriented view. At orthogonal angles (0°, ±90°, 180°) Revit
    /// auto-orients TextNotes correctly, so no rotation is needed. For non-orthogonal
    /// angles the negated ProjectPosition.Angle is applied.
    /// </summary>
    private static double GetTextRotationAngle(Document doc)
    {
        ProjectPosition pp = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
        double angle = pp.Angle;

        double mod = Math.Abs(angle % (Math.PI / 2));
        if (mod < 1e-6 || Math.Abs(mod - Math.PI / 2) < 1e-6)
            return 0;

        return -angle;
    }

    /// <summary>
    /// Offsets a point "below" the main text, in the note's own tilted frame. The offset is a
    /// screen-down half-foot rotated by <paramref name="tilt"/> (Project-North comp + crop
    /// counter-rotation), mapped into model space via the view so it lands directly under the
    /// note the user sees. Identity in an un-rotated view (tilt = 0, mapping = identity).
    /// </summary>
    private static XYZ GetDescriptionPoint(View view, XYZ anchor, double tilt)
    {
        double dx = 0.5 * Math.Sin(tilt);
        double dy = -0.5 * Math.Cos(tilt);
        XYZ model = ViewOrientationHelper.ScreenOffsetToModel(view, new XYZ(dx, dy, 0));
        return new XYZ(anchor.X + model.X, anchor.Y + model.Y, anchor.Z);
    }

    /// <summary>
    /// Rotates a TextNote about the vertical axis by <paramref name="tilt"/> (Project-North
    /// compensation folded together with the view's crop counter-rotation) if it is non-zero.
    /// </summary>
    private static void RotateNote(Document doc, TextNote note, XYZ center, double tilt)
    {
        if (Math.Abs(tilt) < 1e-9) return;
        var axis = Line.CreateBound(center, center + XYZ.BasisZ);
        ElementTransformUtils.RotateElement(doc, note.Id, axis, tilt);
    }
}
