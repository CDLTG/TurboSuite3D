using System.Collections.Generic;

namespace TurboSuite.Snoop.Models;

/// <summary>
/// What a <see cref="SnoopNode"/> represents in the report tree. Kept Revit-agnostic so the
/// Core model can be unit-tested and bound by a future WPF TreeView without a Revit reference.
/// </summary>
public enum SnoopNodeKind
{
    /// <summary>The picked top-level linked element (report root).</summary>
    Family,

    /// <summary>A model/annotation Category — itself a VG checkbox, and the parent of its subcategories.</summary>
    Category,

    /// <summary>A VG subcategory checkbox, nested under its <see cref="Category"/>.</summary>
    Subcategory,

    /// <summary>Diagnostic / status text (e.g. "no geometry for this view", depth cap).</summary>
    Info,
}

/// <summary>
/// One node in the TurboSnoop report tree: the picked family at the root, nested families beneath it
/// (recursively), and Category → Subcategory leaves naming the VG checkbox each piece of geometry rides.
///
/// Pure data, no Revit types — the Revit-facing walk (Shim/Snoop) builds this; a future WPF TreeView
/// binds it. For the current spike it is flattened to indented text by <see cref="SnoopTreeFormatter"/>.
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
