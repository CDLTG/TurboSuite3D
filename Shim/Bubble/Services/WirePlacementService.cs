using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using ElectricalWire = Autodesk.Revit.DB.Electrical.Wire;
using TurboSuite.Bubble.Constants;
using TurboSuite.Bubble.Placement;
using TurboSuite.Shared.Constants;

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
    /// Creates the switchleg wire and anchors its connector-end vertex (v1) at the fixture's chosen
    /// long-axis END rather than the connector center — the end-to-end anchor a drafter sets by hand
    /// on a linear (light-bar) point fixture. Vertex2/Vertex3 are unchanged. Uses the same double-
    /// SetVertex seat technique as <see cref="CreateWireWithOffsetEnd"/>; the electrical connection is
    /// untouched (a display-only vertex move). Callers pass a non-degenerate <paramref name="endPoint"/>.
    /// </summary>
    public static void CreateWireWithLinearEnd(
        Document doc,
        View view,
        IPlacementCalculator placement,
        ElementId wireTypeId,
        Connector fixtureConnector,
        XYZ endPoint)
    {
        var vertices = new List<XYZ>(2) { placement.Vertex2, placement.Vertex3 };
        var wire = ElectricalWire.Create(doc, wireTypeId, view.Id, WiringType.Arc, vertices, fixtureConnector, null);

        var connectorOrigin = fixtureConnector.Origin;
        var endDirection = (endPoint - connectorOrigin).Normalize();

        // Double SetVertex technique: seat with a small nudge toward the end, then the final end.
        wire.SetVertex(0, connectorOrigin + endDirection * BubbleConstants.WireOffsetEndInitialFt);
        wire.SetVertex(0, endPoint);
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

        var scaleFactor = fixture.LookupParameter(ParameterNames.ScaleFactor)?.AsDouble() ?? 1.0;
        var offsetDistance = BubbleConstants.WireOffsetEndWallSconceFt * scaleFactor;

        var connectorOrigin = fixtureConnector.Origin;

        // Double SetVertex technique
        wire.SetVertex(0, connectorOrigin + wallNormal * BubbleConstants.WireOffsetEndInitialFt);
        wire.SetVertex(0, connectorOrigin + wallNormal * offsetDistance);
    }
}
