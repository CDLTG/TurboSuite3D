using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using ElectricalWire = Autodesk.Revit.DB.Electrical.Wire;
using TurboSuite.Shared.Helpers;
using TurboSuite.Wire.Constants;

namespace TurboSuite.Wire.Services;

internal static class WireCreationService
{
    private static ElementId _cachedWireTypeId = ElementId.InvalidElementId;
    private static string? _cachedDocPath;

    private static WireType? GetWireType(Document doc)
    {
        string docPath = doc.PathName ?? doc.Title;
        if (_cachedWireTypeId != ElementId.InvalidElementId && _cachedDocPath == docPath)
        {
            var cached = doc.GetElement(_cachedWireTypeId) as WireType;
            if (cached != null) return cached;
        }

        var wireType = new FilteredElementCollector(doc)
            .OfClass(typeof(WireType))
            .Cast<WireType>()
            .FirstOrDefault();

        if (wireType != null)
        {
            _cachedWireTypeId = wireType.Id;
            _cachedDocPath = docPath;
        }

        return wireType;
    }

    public static Result CreateWire(Document doc, IList<XYZ> points, WiringType wiringType,
        Connector c1, Connector c2, XYZ? wallNormal1, XYZ? wallNormal2,
        double connectorOffset, bool facingSameDirection, ref string message)
    {
        WireType? wireType = GetWireType(doc);
        if (wireType == null)
        {
            message = "No WireType found in project.";
            return Result.Failed;
        }

        using (Transaction t = new Transaction(doc, "Create Wire"))
        {
            t.Start();

            ElectricalWire wire = ElectricalWire.Create(doc, wireType.Id, doc.ActiveView.Id, wiringType, points, c1, c2);

            int vertexCount = wire.NumberOfVertices;

            if (wallNormal1 != null)
            {
                double baseOffset = connectorOffset;
                double initialOffset = 0.5 / 12.0;

                wire.SetVertex(0, c1.Origin + wallNormal1 * initialOffset);
                wire.SetVertex(0, c1.Origin + wallNormal1 * baseOffset);

                XYZ endNormal = facingSameDirection ? wallNormal1 : (wallNormal2 ?? wallNormal1);
                wire.SetVertex(vertexCount - 1, c2.Origin + endNormal * initialOffset);
                wire.SetVertex(vertexCount - 1, c2.Origin + endNormal * baseOffset);
            }

            t.Commit();
        }

        return Result.Succeeded;
    }

    public static void DeleteWiresBetweenFixtures(Document doc, Connector c1, Connector c2)
    {
        HashSet<ElementId> wiresAtC1 = new HashSet<ElementId>();
        foreach (Connector connected in c1.AllRefs)
        {
            if (connected.Owner is ElectricalWire wire)
            {
                wiresAtC1.Add(wire.Id);
            }
        }

        List<ElementId> wiresToDelete = new List<ElementId>();
        foreach (Connector connected in c2.AllRefs)
        {
            if (connected.Owner is ElectricalWire wire && wiresAtC1.Contains(wire.Id))
            {
                wiresToDelete.Add(wire.Id);
            }
        }

        if (wiresToDelete.Count > 0)
        {
            using (Transaction t = new Transaction(doc, "Delete Existing Wires"))
            {
                t.Start();
                doc.Delete(wiresToDelete);
                t.Commit();
            }
        }
    }
}
