#nullable disable
using System.Collections.Generic;

namespace TurboSuite.Schedule.Models;

/// <summary>
/// Hidden <c>_meta</c> sheet payload: which project/version/time last wrote the workbook. Drives the
/// wrong-project warning (workbook <see cref="ProjectPath"/> vs the live <c>doc.PathName</c>).
/// </summary>
public class WorkbookMeta
{
    public string ProjectPath { get; set; } = "";
    public string RevitVersion { get; set; } = "";
    public string LastUpdated { get; set; } = ""; // ISO-8601 text; stored as text so ClosedXML round-trips exactly
}

/// <summary>One data row read from a sheet: its Type Mark key plus every field column keyed by header
/// Label. The Type Mark column itself is <b>not</b> in <see cref="Cells"/> — it is the row key.</summary>
public class WorkbookRow
{
    public string TypeMark { get; set; } = "";

    /// <summary>Header Label → raw cell text (as typed by the designer). Case-insensitive on the header.</summary>
    public Dictionary<string, string> Cells { get; set; } =
        new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
}

/// <summary>One sheet's worth of rows, branded with the <see cref="PageKind"/> it maps to.</summary>
public class WorkbookSheet
{
    public PageKind Kind { get; set; }
    public List<WorkbookRow> Rows { get; set; } = new List<WorkbookRow>();
}

/// <summary>The whole workbook as read: meta + one <see cref="WorkbookSheet"/> per Kind present.</summary>
public class WorkbookSnapshot
{
    public WorkbookMeta Meta { get; set; } = new WorkbookMeta();
    public List<WorkbookSheet> Sheets { get; set; } = new List<WorkbookSheet>();
}

/// <summary>Outcome of an add-only Update (model → file).</summary>
public class WorkbookUpdateResult
{
    /// <summary>Type Marks appended as new rows this run.</summary>
    public List<string> Added { get; set; } = new List<string>();

    /// <summary>Type Marks newly missing this run — flagged red (first grace cycle).</summary>
    public List<string> Flagged { get; set; } = new List<string>();

    /// <summary>Type Marks that were already flagged and are still missing — their rows deleted this run
    /// (second cycle of the one-cycle grace period).</summary>
    public List<string> Purged { get; set; } = new List<string>();
}
