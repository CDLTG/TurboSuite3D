#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using Autodesk.Revit.DB;
using TurboSuite.Name.Models;
using TurboSuite.Shared.Models;
using Line = ACadSharp.Entities.Line;

namespace TurboSuite.Name.Services;

/// <summary>
/// Extracts wall line segments and door/window positions from linked DWG files.
/// </summary>
public static class CadWallExtractorService
{
    public static (List<CadWallSegment> WallSegments, List<XYZ> DoorPositions, List<XYZ> WindowPositions)
        ExtractWallGeometry(Document doc, View view, CadRoomSourceSettings settings)
    {
        var wallSegments = new List<CadWallSegment>();
        var doorPositions = new List<XYZ>();
        var windowPositions = new List<XYZ>();

        var wallLayers = new HashSet<string>(settings.WallLayerNames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var doorLayers = new HashSet<string>(settings.DoorLayerNames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var windowLayers = new HashSet<string>(settings.WindowLayerNames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        if (wallLayers.Count == 0)
            return (wallSegments, doorPositions, windowPositions);

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
            if (extRef?.GetAbsolutePath() == null) continue;

            string dwgPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetAbsolutePath());
            if (!File.Exists(dwgPath)) continue;

            Transform cadTransform = import.GetTransform();
            CadDocument cadDoc;
            using (var reader = new DwgReader(dwgPath))
            {
                cadDoc = reader.Read();
            }

            double unitToFeet = GetUnitToFeetFactor(cadDoc.Header.InsUnits);

            foreach (var entity in cadDoc.Entities)
            {
                string layer = entity.Layer?.Name ?? "";

                if (wallLayers.Contains(layer))
                    ExtractWallEntity(entity, unitToFeet, cadTransform, wallSegments);
                else if (doorLayers.Contains(layer))
                    ExtractPositionEntity(entity, unitToFeet, cadTransform, doorPositions);
                else if (windowLayers.Contains(layer))
                    ExtractPositionEntity(entity, unitToFeet, cadTransform, windowPositions);
            }
        }

        return (wallSegments, doorPositions, windowPositions);
    }

    private static void ExtractWallEntity(Entity entity, double unitToFeet, Transform cadTransform,
        List<CadWallSegment> segments)
    {
        if (entity is Line line)
        {
            var start = TransformPoint(line.StartPoint.X, line.StartPoint.Y, unitToFeet, cadTransform);
            var end = TransformPoint(line.EndPoint.X, line.EndPoint.Y, unitToFeet, cadTransform);
            if (start.DistanceTo(end) > 0.001)
                segments.Add(new CadWallSegment(start, end));
        }
        else if (entity is LwPolyline lwPoly)
        {
            var verts = lwPoly.Vertices.ToList();
            for (int i = 0; i < verts.Count - 1; i++)
            {
                if (Math.Abs(verts[i].Bulge) > 1e-6)
                {
                    TessellateArcVertex(verts[i].Location.X, verts[i].Location.Y,
                        verts[i + 1].Location.X, verts[i + 1].Location.Y,
                        verts[i].Bulge, unitToFeet, cadTransform, segments);
                }
                else
                {
                    var start = TransformPoint(verts[i].Location.X, verts[i].Location.Y, unitToFeet, cadTransform);
                    var end = TransformPoint(verts[i + 1].Location.X, verts[i + 1].Location.Y, unitToFeet, cadTransform);
                    if (start.DistanceTo(end) > 0.001)
                        segments.Add(new CadWallSegment(start, end));
                }
            }
            if (lwPoly.IsClosed && verts.Count > 2)
            {
                int last = verts.Count - 1;
                if (Math.Abs(verts[last].Bulge) > 1e-6)
                {
                    TessellateArcVertex(verts[last].Location.X, verts[last].Location.Y,
                        verts[0].Location.X, verts[0].Location.Y,
                        verts[last].Bulge, unitToFeet, cadTransform, segments);
                }
                else
                {
                    var start = TransformPoint(verts[last].Location.X, verts[last].Location.Y, unitToFeet, cadTransform);
                    var end = TransformPoint(verts[0].Location.X, verts[0].Location.Y, unitToFeet, cadTransform);
                    if (start.DistanceTo(end) > 0.001)
                        segments.Add(new CadWallSegment(start, end));
                }
            }
        }
        else if (entity is ACadSharp.Entities.Arc arc)
        {
            TessellateArc(arc, unitToFeet, cadTransform, segments);
        }
    }

    private static void ExtractPositionEntity(Entity entity, double unitToFeet, Transform cadTransform,
        List<XYZ> positions)
    {
        if (entity is Insert insert)
        {
            positions.Add(TransformPoint(insert.InsertPoint.X, insert.InsertPoint.Y, unitToFeet, cadTransform));
        }
        else if (entity is Line line)
        {
            double mx = (line.StartPoint.X + line.EndPoint.X) / 2;
            double my = (line.StartPoint.Y + line.EndPoint.Y) / 2;
            positions.Add(TransformPoint(mx, my, unitToFeet, cadTransform));
        }
        else if (entity is LwPolyline lwPoly)
        {
            var verts = lwPoly.Vertices.ToList();
            if (verts.Count > 0)
            {
                double cx = verts.Average(v => v.Location.X);
                double cy = verts.Average(v => v.Location.Y);
                positions.Add(TransformPoint(cx, cy, unitToFeet, cadTransform));
            }
        }
        else if (entity is ACadSharp.Entities.Arc arc)
        {
            double midAngle = (arc.StartAngle + arc.EndAngle) / 2;
            double mx = arc.Center.X + arc.Radius * Math.Cos(midAngle);
            double my = arc.Center.Y + arc.Radius * Math.Sin(midAngle);
            positions.Add(TransformPoint(mx, my, unitToFeet, cadTransform));
        }
    }

    private static void TessellateArc(ACadSharp.Entities.Arc arc, double unitToFeet,
        Transform cadTransform, List<CadWallSegment> segments)
    {
        double startAngle = arc.StartAngle;
        double endAngle = arc.EndAngle;
        if (endAngle <= startAngle) endAngle += 2 * Math.PI;
        double span = endAngle - startAngle;
        int numSegments = Math.Max(4, (int)(span / (Math.PI / 8)));

        for (int i = 0; i < numSegments; i++)
        {
            double a1 = startAngle + span * i / numSegments;
            double a2 = startAngle + span * (i + 1) / numSegments;
            double x1 = arc.Center.X + arc.Radius * Math.Cos(a1);
            double y1 = arc.Center.Y + arc.Radius * Math.Sin(a1);
            double x2 = arc.Center.X + arc.Radius * Math.Cos(a2);
            double y2 = arc.Center.Y + arc.Radius * Math.Sin(a2);
            var start = TransformPoint(x1, y1, unitToFeet, cadTransform);
            var end = TransformPoint(x2, y2, unitToFeet, cadTransform);
            if (start.DistanceTo(end) > 0.001)
                segments.Add(new CadWallSegment(start, end));
        }
    }

    private static void TessellateArcVertex(double x1, double y1, double x2, double y2,
        double bulge, double unitToFeet, Transform cadTransform, List<CadWallSegment> segments)
    {
        // Compute arc from bulge factor
        double dx = x2 - x1;
        double dy = y2 - y1;
        double chord = Math.Sqrt(dx * dx + dy * dy);
        if (chord < 1e-9) return;

        double sagitta = Math.Abs(bulge) * chord / 2;
        double radius = (chord * chord / 4 + sagitta * sagitta) / (2 * sagitta);
        double mx = (x1 + x2) / 2;
        double my = (y1 + y2) / 2;
        double nx = -dy / chord;
        double ny = dx / chord;
        double d = radius - sagitta;
        double sign = bulge > 0 ? 1 : -1;
        double cx = mx + sign * d * nx;
        double cy = my + sign * d * ny;

        double a1 = Math.Atan2(y1 - cy, x1 - cx);
        double a2 = Math.Atan2(y2 - cy, x2 - cx);

        if (bulge > 0)
        {
            if (a2 <= a1) a2 += 2 * Math.PI;
        }
        else
        {
            if (a1 <= a2) a1 += 2 * Math.PI;
        }

        double span = Math.Abs(a2 - a1);
        int numSegs = Math.Max(4, (int)(span / (Math.PI / 8)));
        double step = (a2 - a1) / numSegs;

        for (int i = 0; i < numSegs; i++)
        {
            double aa1 = a1 + step * i;
            double aa2 = a1 + step * (i + 1);
            var start = TransformPoint(cx + radius * Math.Cos(aa1), cy + radius * Math.Sin(aa1), unitToFeet, cadTransform);
            var end = TransformPoint(cx + radius * Math.Cos(aa2), cy + radius * Math.Sin(aa2), unitToFeet, cadTransform);
            if (start.DistanceTo(end) > 0.001)
                segments.Add(new CadWallSegment(start, end));
        }
    }

    private static XYZ TransformPoint(double x, double y, double unitToFeet, Transform cadTransform)
    {
        var cadPt = new XYZ(x * unitToFeet, y * unitToFeet, 0);
        return cadTransform.OfPoint(cadPt);
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
            _ => 1.0 / 12.0,
        };
    }
}
