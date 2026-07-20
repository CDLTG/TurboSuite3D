#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using Autodesk.Revit.DB;
using TurboSuite.Name.Models;
using TurboSuite.Name.Regions;
using TurboSuite.Shared.Models;

namespace TurboSuite.Name.Services;

/// <summary>
/// Extracts room names and ceiling heights from linked DWG files.
/// Supports Block mode (INSERT attributes) and Text mode (layer-based text).
/// </summary>
public static class CadRoomExtractorService
{
    public static List<CadRoomData> ExtractRoomData(Document doc, View view, CadRoomSourceSettings settings)
    {
        var results = new List<CadRoomData>();

        var cadLinks = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(ImportInstance))
            .Cast<ImportInstance>()
            .Where(ii => ii.IsLinked)
            .ToList();

        foreach (var import in cadLinks)
        {
            var typeId = import.GetTypeId();
            var cadLinkType = doc.GetElement(typeId) as CADLinkType;
            if (cadLinkType == null) continue;

            var extRef = cadLinkType.GetExternalFileReference();
            if (extRef == null || extRef.GetAbsolutePath() == null) continue;

            string dwgPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetAbsolutePath());
            if (!File.Exists(dwgPath)) continue;

            // TurboName-9: scope names and heights to their own link independently. The room extractor used to
            // read EVERY linked DWG, so a plan + RCP sharing a room-name layer seeded each room twice and split
            // it in half. Blank scope = all links. Skip the (expensive) DWG read when this link supplies neither.
            string dwgFile = Path.GetFileName(dwgPath);
            bool includeNames = CadLinkScope.Includes(settings.RoomNameLinkName, dwgFile);
            bool includeHeights = CadLinkScope.Includes(settings.CeilingHeightLinkName, dwgFile);
            if (!includeNames && !includeHeights) continue;

            Transform cadTransform = import.GetTransform();

            CadDocument cadDoc;
            try
            {
                using (var reader = new DwgReader(dwgPath))
                {
                    cadDoc = reader.Read();
                }
            }
            catch (IOException)
            {
                string fileName = Path.GetFileName(dwgPath);
                throw new IOException(
                    $"Cannot read \"{fileName}\" because it is open in another application.\n\n" +
                    "Close the file in AutoCAD and try again.");
            }

            double unitToFeet = GetUnitToFeetFactor(cadDoc.Header.InsUnits);

            if (settings.Mode == "Block")
                ExtractBlockMode(cadDoc, cadTransform, unitToFeet, settings, includeNames, includeHeights, results);
            else
                ExtractTextMode(cadDoc, cadTransform, unitToFeet, settings, includeNames, includeHeights, results);
        }

        return results;
    }

    private static void ExtractBlockMode(CadDocument cadDoc, Transform cadTransform,
        double unitToFeet, CadRoomSourceSettings settings, bool includeNames, bool includeHeights,
        List<CadRoomData> results)
    {
        foreach (var entity in cadDoc.Entities)
        {
            if (entity is not Insert insert) continue;
            string blockName = insert.Block?.Name ?? "";
            if (!string.Equals(blockName, settings.BlockName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (insert.Attributes == null || insert.Attributes.Count == 0) continue;

            var attrDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attr in insert.Attributes)
            {
                string tag = attr.Tag ?? "";
                string val = StripCadFormatting(attr.Value ?? "");
                if (!string.IsNullOrEmpty(tag))
                    attrDict[tag] = val;
            }

            // Concatenate room name tags (space-separated) — only when this link supplies names.
            string roomName = "";
            if (includeNames && settings.RoomNameTags != null && settings.RoomNameTags.Count > 0)
            {
                var parts = settings.RoomNameTags
                    .Select(tag => attrDict.TryGetValue(tag, out var v) ? v.Trim() : "")
                    .Where(s => !string.IsNullOrEmpty(s));
                roomName = string.Join(" ", parts).Replace("#", "").ToUpper();
            }

            // Read ceiling height — only when this link supplies heights.
            string ceilingHeight = "";
            if (includeHeights && !string.IsNullOrEmpty(settings.CeilingHeightTag)
                && attrDict.TryGetValue(settings.CeilingHeightTag, out var ch))
            {
                ceilingHeight = ch.Trim();
            }

            // Skip if both are empty
            if (string.IsNullOrEmpty(roomName) && string.IsNullOrEmpty(ceilingHeight))
                continue;

            // Transform INSERT point to Revit coordinates
            double cadX = insert.InsertPoint.X;
            double cadY = insert.InsertPoint.Y;
            var cadPointFeet = new XYZ(cadX * unitToFeet, cadY * unitToFeet, 0);
            var revitPoint = cadTransform.OfPoint(cadPointFeet);

            results.Add(new CadRoomData(roomName, ceilingHeight, revitPoint));
        }
    }

    private static void ExtractTextMode(CadDocument cadDoc, Transform cadTransform,
        double unitToFeet, CadRoomSourceSettings settings, bool includeNames, bool includeHeights,
        List<CadRoomData> results)
    {
        // Room-name text is collected in CAD space (feet, pre-transform) because RoomLabelGrouping measures
        // along the TEXT's own axes — a DWG inserted into Revit at a rotation would tilt them. The cluster
        // anchors are transformed to Revit coordinates once grouping is done.
        var roomNameLabels = new List<LabelText>();
        var ceilingTexts = new List<(string Text, XYZ Point)>();

        bool hasCeilingLayer = !string.IsNullOrEmpty(settings.CeilingHeightLayer);
        bool hasCeilingBlock = !string.IsNullOrEmpty(settings.CeilingHeightBlockName)
                            && !string.IsNullOrEmpty(settings.CeilingHeightBlockTag);
        bool sameLayer = hasCeilingLayer && string.Equals(settings.RoomNameLayer, settings.CeilingHeightLayer,
            StringComparison.OrdinalIgnoreCase);

        foreach (var entity in cadDoc.Entities)
        {
            // Extract ceiling heights from block attributes
            if (hasCeilingBlock && entity is Insert insert)
            {
                string blockName = insert.Block?.Name ?? "";
                if (string.Equals(blockName, settings.CeilingHeightBlockName, StringComparison.OrdinalIgnoreCase)
                    && insert.Attributes != null)
                {
                    foreach (var attr in insert.Attributes)
                    {
                        if (string.Equals(attr.Tag, settings.CeilingHeightBlockTag, StringComparison.OrdinalIgnoreCase))
                        {
                            string heightVal = StripCadFormatting(attr.Value ?? "").Trim();
                            if (includeHeights && !string.IsNullOrEmpty(heightVal))
                            {
                                double cadX = insert.InsertPoint.X;
                                double cadY = insert.InsertPoint.Y;
                                var cadPointFeet = new XYZ(cadX * unitToFeet, cadY * unitToFeet, 0);
                                var revitPoint = cadTransform.OfPoint(cadPointFeet);
                                ceilingTexts.Add((heightVal, revitPoint));
                            }
                            break;
                        }
                    }
                }
                continue;
            }

            // Extract text entities
            var extracted = ExtractTextFromEntity(entity);
            if (extracted == null) continue;

            var (text, x, y, layer, height) = extracted.Value;
            var textPointFeet = new XYZ(x * unitToFeet, y * unitToFeet, 0);

            if (string.Equals(layer, settings.RoomNameLayer, StringComparison.OrdinalIgnoreCase))
            {
                // sameLayer mode: heights share the room-name layer. Split height-shaped text (leads with a
                // digit/'+' AND has a '/") off to ceilingTexts so it isn't treated as a room name and doesn't
                // seed its own watershed owner (the TurboName-5 split, TurboName-8). Outside sameLayer this is
                // never hit for heights — two-layer/block modes capture them via the branch below / above.
                if (sameLayer && CeilingHeightFormatter.LooksLikeHeight(text))
                {
                    if (includeHeights)
                        ceilingTexts.Add((text, cadTransform.OfPoint(textPointFeet)));
                }
                else if (includeNames)
                {
                    // Normalize to the FINAL room name before grouping — the horizontal gate measures against
                    // string length, so it has to see the same string that ends up on the region.
                    roomNameLabels.Add(new LabelText(
                        new Pt(textPointFeet.X, textPointFeet.Y),
                        text.Replace("#", "").ToUpper(),
                        height * unitToFeet));
                }
            }

            if (includeHeights && hasCeilingLayer && !sameLayer && !hasCeilingBlock
                && string.Equals(layer, settings.CeilingHeightLayer, StringComparison.OrdinalIgnoreCase))
                ceilingTexts.Add((text, cadTransform.OfPoint(textPointFeet)));
        }

        // Coalesce the separate text entities that make up one multi-line room label ("BAR/BREAKFAST" over
        // "AREA") into a single entry. Without this each line seeds its own watershed owner and splits the
        // room in half, and the manual naming pass sees two names in one region and skips it as ambiguous.
        // Applies in sameLayer mode too now that height-shaped text has been split off above (TurboName-8), so
        // roomNameLabels holds only names — nothing can merge a "10'-0"" into a label.
        var clusters = RoomLabelGrouping.Group(roomNameLabels);

        foreach (var c in clusters)
            results.Add(new CadRoomData(
                c.Text, "", cadTransform.OfPoint(new XYZ(c.Anchor.X, c.Anchor.Y, 0))));

        // Separate ceiling-height entries at their own locations. `ceilingTexts` is populated above only when
        // there is a ceiling source distinct from the room-name text — a different layer, or a block — so the
        // "no ceiling source" and "same layer, no block" cases contribute nothing here and this loop is a
        // no-op for them. That is what collapses the three original emit branches into these two loops.
        foreach (var (heightText, heightPoint) in ceilingTexts)
            results.Add(new CadRoomData("", heightText, heightPoint));
    }

    private static (string Text, double X, double Y, string Layer, double Height)? ExtractTextFromEntity(Entity entity)
    {
        string text = null;
        double x = 0, y = 0, height = 0;
        string layer = entity.Layer?.Name ?? "";

        if (entity is TextEntity textEntity)
        {
            text = textEntity.Value;
            x = textEntity.InsertPoint.X;
            y = textEntity.InsertPoint.Y;
            height = textEntity.Height;
        }
        else if (entity is MText mtext)
        {
            text = mtext.Value;
            x = mtext.InsertPoint.X;
            y = mtext.InsertPoint.Y;
            height = mtext.Height;
        }

        if (text == null) return null;
        text = StripCadFormatting(text);
        return (text.Trim(), x, y, layer, height);
    }

    private static double GetUnitToFeetFactor(ACadSharp.Types.Units.UnitsType units)
    {
        return units switch
        {
            ACadSharp.Types.Units.UnitsType.Inches => 1.0 / 12.0,
            ACadSharp.Types.Units.UnitsType.Feet => 1.0,
            ACadSharp.Types.Units.UnitsType.Millimeters => 1.0 / 304.8,
            ACadSharp.Types.Units.UnitsType.Centimeters => 1.0 / 30.48,
            ACadSharp.Types.Units.UnitsType.Meters => 1.0 / 0.3048,
            _ => 1.0 / 12.0, // default to inches
        };
    }

    public static string StripCadFormatting(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // AutoCAD %% escape codes
        text = text.Replace("%%U", "").Replace("%%u", "");
        text = text.Replace("%%O", "").Replace("%%o", "");
        text = text.Replace("%%D", "\u00B0").Replace("%%d", "\u00B0");
        text = text.Replace("%%P", "\u00B1").Replace("%%p", "\u00B1");
        text = text.Replace("%%C", "\u2205").Replace("%%c", "\u2205");

        // MText formatting codes.
        // The font code is \fName|b1|i0|c0|p34; and real DWGs carry it BOTH braced and bare — a measured job
        // had a room label whose second line was stored as "\fRaleway|b1|i0|c0|p34|;3" with no brace. So the
        // brace is optional here; requiring "{\f" let that code through verbatim and the room got named with
        // the literal escape instead of "3" (written to Comments AND stamped as a TextNote). Also accepts \F.
        // The class on the next line covers neither f nor F, so this is the only site that handles the font code.
        text = Regex.Replace(text, @"\{?\\[fF][^;]*;", "");
        // Split the code class by terminator shape: H/W/Q/T/C take a ;-terminated value (\H1.5x;), while
        // L/O/K (underline/overline/strikethrough, both cases) are standalone toggles with NO terminator.
        // Folding the toggles into the valued class let a bare \L run [^;]* forward and delete every character
        // up to the next semicolon anywhere downstream.
        text = Regex.Replace(text, @"\\[HWQTC][^;]*;", "");
        text = Regex.Replace(text, @"\\[LlOoKk]", "");
        text = text.Replace("\\P", " ");
        text = Regex.Replace(text, @"\\p[^;]*;", "");
        text = Regex.Replace(text, @"\\A\d;", "");
        text = text.Replace("{", "").Replace("}", "");
        text = Regex.Replace(text, @"\s+", " ").Trim();

        return text;
    }
}
