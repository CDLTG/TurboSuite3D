using Autodesk.Revit.DB;

namespace TurboSuite.Bubble.Placement;

/// <summary>
/// Interface for placement calculation strategies.
/// </summary>
internal interface IPlacementCalculator
{
    XYZ FixturePoint { get; }
    double Rotation { get; }
    bool RotatesWithComponent { get; }
    XYZ NewTagPosition { get; }
    XYZ Vertex2 { get; }
    XYZ Vertex3 { get; }
    bool IsFlipped { get; }
    void CalculateFinalPositions(XYZ flipPoint);
}
