using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using ElectricalWire = Autodesk.Revit.DB.Electrical.Wire;
using TurboSuite.Bubble.Constants;
using TurboSuite.Bubble.Placement;

namespace TurboSuite.Bubble.Services;

/// <summary>
/// Service for creating wire connections for switchleg bubbles.
/// </summary>
internal static class WirePlacementService
{
    public static void CreateWire(
        Document doc,
        View view,
        IPlacementCalculator placement,
        ElementId wireTypeId,
        Connector fixtureConnector)
    {
        var vertices = new List<XYZ>(2) { placement.Vertex2, placement.Vertex3 };
        ElectricalWire.Create(doc, wireTypeId, view.Id, WiringType.Arc, vertices, fixtureConnector, null);
    }

    /// <summary>
    /// Creates a wire with adjusted offset end for line-based fixtures.
    /// </summary>
    public static void CreateWireWithOffsetEnd(
        Document doc,
        View view,
        IPlacementCalculator placement,
        ElementId wireTypeId,
        Connector fixtureConnector)
    {
        var vertices = new List<XYZ>(2) { placement.Vertex2, placement.Vertex3 };
        var wire = ElectricalWire.Create(doc, wireTypeId, view.Id, WiringType.Arc, vertices, fixtureConnector, null);

        var connectorOrigin = fixtureConnector.Origin;
        var offsetDirection = (placement.Vertex2 - connectorOrigin).Normalize();

        // Double SetVertex technique
        wire.SetVertex(0, connectorOrigin + offsetDirection * BubbleConstants.WireOffsetEndInitialFt);
        wire.SetVertex(0, connectorOrigin + offsetDirection * BubbleConstants.WireOffsetEndFinalFt);
    }

    /// <summary>
    /// Creates a wire with offset end for wall sconce fixtures.
    /// </summary>
    public static void CreateWireWithWallSconceOffset(
        Document doc,
        View view,
        IPlacementCalculator placement,
        ElementId wireTypeId,
        Connector fixtureConnector,
        XYZ wallNormal,
        FamilyInstance fixture)
    {
        var vertices = new List<XYZ>(2) { placement.Vertex2, placement.Vertex3 };
        var wire = ElectricalWire.Create(doc, wireTypeId, view.Id, WiringType.Arc, vertices, fixtureConnector, null);

        var scaleFactor = fixture.LookupParameter("Scale Factor")?.AsDouble() ?? 1.0;
        var offsetDistance = BubbleConstants.WireOffsetEndWallSconceFt * scaleFactor;

        var connectorOrigin = fixtureConnector.Origin;

        // Double SetVertex technique
        wire.SetVertex(0, connectorOrigin + wallNormal * BubbleConstants.WireOffsetEndInitialFt);
        wire.SetVertex(0, connectorOrigin + wallNormal * offsetDistance);
    }
}
