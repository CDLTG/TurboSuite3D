using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace TurboSuite.Shared.Filters;

/// <summary>
/// Selection filter that accepts only linked CAD imports (<see cref="ImportInstance"/> with
/// <c>IsLinked == true</c>). Used by the Settings dialog's "Pick from view" room-source discovery.
/// </summary>
/// <remarks>
/// A <c>PickObject(PointOnElement)</c> on an import resolves through <see cref="AllowReference"/>
/// (the geometry reference), NOT <see cref="AllowElement"/>, so the reference path must also pass —
/// otherwise every room-label click is silently rejected. When a document is supplied the reference
/// is re-checked against the same linked-import rule.
/// </remarks>
public class ImportInstanceSelectionFilter : ISelectionFilter
{
    private readonly Document? _doc;

    public ImportInstanceSelectionFilter(Document? doc = null) => _doc = doc;

    public bool AllowElement(Element elem) => elem is ImportInstance ii && ii.IsLinked;

    public bool AllowReference(Reference reference, XYZ position)
    {
        if (_doc == null) return true;
        return _doc.GetElement(reference.ElementId) is ImportInstance ii && ii.IsLinked;
    }
}
