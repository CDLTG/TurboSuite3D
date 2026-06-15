#nullable disable
using System.Collections.Generic;

namespace TurboSuite.Schedule.Models;

/// <summary>Granularity of an in-app copy.</summary>
public enum ClipboardScope
{
    Type,
    Section
}

/// <summary>
/// In-app copy/paste snapshot (no Revit/OS clipboard). Captured from the page being viewed; pasted
/// onto the current page as <i>dirty edits</i>. Values are keyed by <see cref="FieldDef.ParamKey"/>
/// so paste matches the target's fields regardless of label. Source-<c>⟨varies⟩</c> and locked fields
/// are excluded at copy time, so they never overwrite a target.
/// </summary>
public class SpecClipboard
{
    public ClipboardScope Scope { get; set; }
    public PageKind SourceKind { get; set; }

    /// <summary>Null for a whole-type copy; set for a section copy.</summary>
    public SpecSection? Section { get; set; }

    /// <summary>ParamKey → value. Only copyable (editable, non-varies) fields are present.</summary>
    public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>();

    /// <summary>Short human label for the status line, e.g. "type L4" / "Notes".</summary>
    public string Descriptor { get; set; }
}
