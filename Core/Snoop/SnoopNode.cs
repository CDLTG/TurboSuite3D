using System.Collections.Generic;

namespace TurboSuite.Snoop.Models;

/// <summary>
/// What a <see cref="SnoopNode"/> represents in the report tree. Kept Revit-agnostic so the
/// Core model can be unit-tested and bound by a future WPF TreeView without a Revit reference.
/// </summary>
public enum SnoopNodeKind
{
    /// <summary>Report root — labelled with the picked family's category.</summary>
    Family,

    /// <summary>A model/annotation Category — itself a VG checkbox, and the parent of its subcategories.</summary>
    Category,

    /// <summary>A VG subcategory checkbox, nested under its <see cref="Category"/>.</summary>
    Subcategory,

    /// <summary>Non-checkbox text: the two section headers and the "(none)" placeholder.</summary>
    Info,
}

/// <summary>
/// One node in the TurboSnoop report tree. Pure data, no Revit types — the Revit-facing walk (Shim/Snoop)
/// builds it; the window binds it via <c>SnoopNodeViewModel</c>.
/// </summary>
public sealed class SnoopNode
{
    public SnoopNode(string label, SnoopNodeKind kind)
    {
        Label = label;
        Kind = kind;
    }

    public string Label { get; }

    public SnoopNodeKind Kind { get; }

    public List<SnoopNode> Children { get; } = new();
}
