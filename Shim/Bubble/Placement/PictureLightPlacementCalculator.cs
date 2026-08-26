using System;
using Autodesk.Revit.DB;
using TurboSuite.Bubble.Constants;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Bubble.Placement;

/// <summary>
/// Placement for picture lights — linear wall fixtures whose plan symbol is symmetric ALONG the wall
/// but extends entirely AWAY from it (the origin/connector sits on the wall-side edge). The spike
/// showed the 2D (Z_Picture Light) and 3D (AL_Decorative_Picture Light (Hosted)) families are
/// byte-for-byte identical in wall-normal terms — symmetric ±half along the wall, a measured room-side
/// depth perpendicular, 0 toward the wall — so ONE wall-aware path serves both and unifies their tag
/// placement. Both families yield a usable wall normal (3D via Hand×Facing, 2D via the facing fallback).
///
/// Everything is built in the wall frame: X = along the wall (`_wallParallel`), Y = wall normal
/// (`_wallNormal`, pointing into the room). The perpendicular clearances are MEASURED from the origin
/// to the real symbol edge (asymmetry-aware, not half of the total extent), so the switchleg wire and
/// bubble stand off past the room-side body and away from the wall (the "2D look"), rather than looping
/// back toward the origin. Sconces/mirrors stay on <see cref="VerticalFacePlacementCalculator"/>.
/// </summary>
internal class PictureLightPlacementCalculator : IPlacementCalculator
{
    public XYZ FixturePoint { get; }
    public double Rotation { get; }
    public bool RotatesWithComponent { get; }

    public XYZ NewTagPosition { get; private set; } = XYZ.Zero;
    public XYZ Vertex2 { get; private set; } = XYZ.Zero;
    public XYZ Vertex3 { get; private set; } = XYZ.Zero;
    public bool IsFlipped { get; private set; }

    /// <summary>The chosen bar end where the wire's connector-end vertex (v1) is seated.</summary>
    public XYZ WireEndPoint { get; private set; } = XYZ.Zero;

    private readonly XYZ _wallNormal;   // away from wall (into the room)
    private readonly XYZ _wallParallel; // along the wall
    private readonly double _halfAlongWall; // origin -> symbol edge along the wall (symmetric)
    private readonly double _roomDepth;     // origin -> symbol edge away from the wall (the asymmetry)

    public PictureLightPlacementCalculator(Document doc, View view, FamilyInstance fixture, IndependentTag sourceTag)
    {
        FixturePoint = GeometryHelper.GetFixtureLocation(fixture)
            ?? throw new InvalidOperationException("Picture light has no valid location for placement.");

        RotatesWithComponent = PlacementCalculatorBase.DetermineRotationMode(sourceTag);

        // Wall frame from the fixture's own transform (mirror-corrected). Works for the hosted 3D
        // family (Hand×Facing) and the unhosted 2D family (facing fallback) alike — see the spike.
        _wallNormal = GeometryHelper.GetWallFaceNormalFromTransform(fixture);
        _wallParallel = new XYZ(-_wallNormal.Y, _wallNormal.X, 0);

        // Tag glyph aligns to the wall (X along wall).
        Rotation = Math.Atan2(_wallParallel.Y, _wallParallel.X);

        // Measured origin->edge extents (asymmetry-aware). Along the wall is symmetric, so take the
        // larger of the two sides; perpendicular uses the room-side depth only.
        double alongPos = GeometryHelper.GetSymbolExtentInDirection(fixture, view, _wallParallel, BubbleConstants.DefaultSymbolSizeFt);
        double alongNeg = GeometryHelper.GetSymbolExtentInDirection(fixture, view, _wallParallel.Negate(), BubbleConstants.DefaultSymbolSizeFt);
        _halfAlongWall = Math.Max(alongPos, alongNeg);
        _roomDepth = GeometryHelper.GetSymbolExtentInDirection(fixture, view, _wallNormal, BubbleConstants.DefaultSymbolSizeFt);
    }

    public void CalculateFinalPositions(XYZ flipPoint)
    {
        // The user's click picks which side along the wall the bubble sits on.
        double side = (flipPoint - FixturePoint).DotProduct(_wallParallel) >= 0 ? 1.0 : -1.0;
        IsFlipped = side < 0;

        // Bubble stands off past the MEASURED room-side depth so it clears the bar into open room.
        double tagOffWall = _roomDepth + BubbleConstants.PictureLightTagClearanceFt;

        // Tag: past the bar end along the wall, cleared past the room-side edge (the 2D look).
        NewTagPosition = FixturePoint
            + _wallParallel * (side * (_halfAlongWall + BubbleConstants.PictureLightTagAlongWallGapFt))
            + _wallNormal * tagOffWall;

        // v3: elbow, inset from the bubble back along the wall at the same depth.
        Vertex3 = NewTagPosition - _wallParallel * (side * BubbleConstants.WireElbowOffsetFt);

        // v2 (arc control): at the elbow column, held shallow so the wire arcs down from the bar end.
        double elbowAlong = _halfAlongWall + BubbleConstants.PictureLightTagAlongWallGapFt - BubbleConstants.WireElbowOffsetFt;
        Vertex2 = FixturePoint
            + _wallParallel * (side * elbowAlong)
            + _wallNormal * BubbleConstants.PictureLightWireMidOffWallFt;

        // v1: the bar end (origin is centered along the wall), exiting near the room-side edge.
        WireEndPoint = FixturePoint
            + _wallParallel * (side * _halfAlongWall)
            + _wallNormal * BubbleConstants.PictureLightWireEndOffWallFt;
    }
}
