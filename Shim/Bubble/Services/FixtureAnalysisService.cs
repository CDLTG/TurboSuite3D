using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace TurboSuite.Bubble.Services;

/// <summary>
/// Service for finding tag types and wire types in the project.
/// </summary>
internal static class FixtureAnalysisService
{
    public static ElementId? FindFirstWireType(Document doc)
    {
        using var collector = new FilteredElementCollector(doc);
        var id = collector
            .OfCategory(BuiltInCategory.OST_Wire)
            .OfClass(typeof(WireType))
            .FirstElementId();

        return id != ElementId.InvalidElementId ? id : null;
    }
}
