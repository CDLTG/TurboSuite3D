#nullable disable
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace TurboSuite.Setup.Models;

/// <summary>Which kind of plan view a generated view is — drives template + linked-view filtering.</summary>
internal enum ViewKind
{
    Floor,
    Rcp
}

/// <summary>
/// A level the user chose to set up, with its computed index. Produced after Stage 1, carried
/// through indexing, view generation, and link-graphics configuration.
/// </summary>
internal sealed class SelectedLevel
{
    /// <summary>Id of the source <see cref="Level"/> inside the linked document.</summary>
    public ElementId SourceLevelId { get; set; }

    public string Name { get; set; }

    public double Elevation { get; set; }

    /// <summary>Two-digit index string ("01", "00", "-01", ...) assigned by <c>LevelIndexer</c>.</summary>
    public string Index { get; set; }
}

/// <summary>
/// One host view TurboSetup intends to create — name, kind, and the source level it belongs to.
/// Stage 2 maps each of these to a linked view (or leaves it unmapped).
/// </summary>
internal sealed class PlannedView
{
    public string ViewName { get; set; }

    public ViewKind Kind { get; set; }

    /// <summary>The source (linked) level this view is generated on.</summary>
    public ElementId SourceLevelId { get; set; }

    public string LevelName { get; set; }
}

/// <summary>Outcome counts from an execution, shown in the post-run summary.</summary>
internal sealed class SetupResult
{
    public int LevelsCopied { get; set; }
    public int ViewsCreated { get; set; }
    public int ViewsSkippedExisting { get; set; }

    /// <summary>Views created where the user chose no linked view (left on host default V/G).</summary>
    public int ViewsUnmapped { get; set; }

    /// <summary>Link overrides successfully written.</summary>
    public int LinkMappingsApplied { get; set; }

    /// <summary>Views where a linked view was chosen but the override write was rejected by Revit.</summary>
    public int LinkApplyFailures { get; set; }

    /// <summary>First link-override error message, captured to surface the real cause.</summary>
    public string LinkErrorSample { get; set; }

    /// <summary>True when the running Revit (2024) can't set Custom link overrides — link step skipped.</summary>
    public bool LinkStepUnavailable { get; set; }

    public List<string> Notes { get; } = new List<string>();
}
