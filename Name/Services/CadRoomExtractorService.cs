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

            Transform cadTransform = import.GetTransform();

            CadDocument cadDoc;
            using (var reader = new DwgReader(dwgPath))
            {
                cadDoc = reader.Read();
            }

            double unitToFeet = GetUnitToFeetFactor(cadDoc.Header.InsUnits);

            if (settings.Mode == "Block")
                ExtractBlockMode(cadDoc, cadTransform, unitToFeet, settings, results);
            else
                ExtractTextMode(cadDoc, cadTransform, unitToFeet, settings, results);
        }

        return results;
    }

    private static void ExtractBlockMode(CadDocument cadDoc, Transform cadTransform,
        double unitToFeet, CadRoomSourceSettings settings, List<CadRoomData> results)
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

            // Concatenate room name tags (space-separated)
            string roomName = "";
            if (settings.RoomNameTags != null && settings.RoomNameTags.Count > 0)
            {
                var parts = settings.RoomNameTags
                    .Select(tag => attrDict.TryGetValue(tag, out var v) ? v.Trim() : "")
                    .Where(s => !string.IsNullOrEmpty(s));
                roomName = string.Join(" ", parts).Replace("#", "").ToUpper();
            }

            // Read ceiling height
            string ceilingHeight = "";
            if (!string.IsNullOrEmpty(settings.CeilingHeightTag)
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
        double unitToFeet, CadRoomSourceSettings settings, List<CadRoomData> results)
    {
        var roomNameTexts = new List<(string Text, XYZ Point)>();
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
                            if (!string.IsNullOrEmpty(heightVal))
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

            var (text, x, y, layer) = extracted.Value;
            var textPointFeet = new XYZ(x * unitToFeet, y * unitToFeet, 0);
            var revitPoint2 = cadTransform.OfPoint(textPointFeet);

            if (string.Equals(layer, settings.RoomNameLayer, StringComparison.OrdinalIgnoreCase))
                roomNameTexts.Add((text, revitPoint2));

            if (hasCeilingLayer && !sameLayer && !hasCeilingBlock
                && string.Equals(layer, settings.CeilingHeightLayer, StringComparison.OrdinalIgnoreCase))
                ceilingTexts.Add((text, revitPoint2));
        }

        if (!hasCeilingLayer && !hasCeilingBlock)
        {
            // No ceiling height source — room names only
            foreach (var (text, point) in roomNameTexts)
                results.Add(new CadRoomData(text.Replace("#", "").ToUpper(), "", point));
        }
        else if (sameLayer && !hasCeilingBlock)
        {
            // All text on the same layer — each text is a room name, no ceiling height pairing
            foreach (var (text, point) in roomNameTexts)
                results.Add(new CadRoomData(text.Replace("#", "").ToUpper(), "", point));
        }
        else
        {
            // Room names as entries, plus separate ceiling height entries at their own locations
            foreach (var (name, namePoint) in roomNameTexts)
                results.Add(new CadRoomData(name.Replace("#", "").ToUpper(), "", namePoint));

            foreach (var (heightText, heightPoint) in ceilingTexts)
                results.Add(new CadRoomData("", heightText, heightPoint));
        }
    }

    private static (string Text, double X, double Y, string Layer)? ExtractTextFromEntity(Entity entity)
    {
        string text = null;
        double x = 0, y = 0;
        string layer = entity.Layer?.Name ?? "";

        if (entity is TextEntity textEntity)
        {
            text = textEntity.Value;
            x = textEntity.InsertPoint.X;
            y = textEntity.InsertPoint.Y;
        }
        else if (entity is MText mtext)
        {
            text = mtext.Value;
            x = mtext.InsertPoint.X;
            y = mtext.InsertPoint.Y;
        }

        if (text == null) return null;
        text = StripCadFormatting(text);
        return (text.Trim(), x, y, layer);
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

    private static string StripCadFormatting(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // AutoCAD %% escape codes
        text = text.Replace("%%U", "").Replace("%%u", "");
        text = text.Replace("%%O", "").Replace("%%o", "");
        text = text.Replace("%%D", "\u00B0").Replace("%%d", "\u00B0");
        text = text.Replace("%%P", "\u00B1").Replace("%%p", "\u00B1");
        text = text.Replace("%%C", "\u2205").Replace("%%c", "\u2205");

        // MText formatting codes
        text = Regex.Replace(text, @"\{\\f[^;]*;", "");
        text = Regex.Replace(text, @"\\[HWQTCLOK][^;]*;", "");
        text = text.Replace("\\P", " ");
        text = Regex.Replace(text, @"\\p[^;]*;", "");
        text = Regex.Replace(text, @"\\A\d;", "");
        text = text.Replace("{", "").Replace("}", "");
        text = Regex.Replace(text, @"\s+", " ").Trim();

        return text;
    }
}
