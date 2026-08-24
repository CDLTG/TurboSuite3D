#nullable disable
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Schedule.Models;

/// <summary>
/// Outcome of one press of the single reconcile button: the Sync half (workbook → model,
/// <see cref="SyncReport"/>) and the Update half (model → workbook, <see cref="Update"/>), plus the
/// re-collected pages for the form auto-refresh. On a first run / recreate it is <see cref="SeededOnly"/>
/// (no Sync half, no model write). Status/detail composition lives here so the VM stays thin.
/// </summary>
public class ReconcileResult
{
    /// <summary>The workbook→model half. Null on a seed-only run.</summary>
    public SyncReport SyncReport { get; set; }

    /// <summary>The model→workbook half (append / flag / purge). Always present.</summary>
    public WorkbookUpdateResult Update { get; set; } = new WorkbookUpdateResult();

    /// <summary>Freshly re-collected pages for the form refresh. Null when no model write happened
    /// (seed-only, or a blocking Sync).</summary>
    public IReadOnlyList<FixtureTypeSpec> Refreshed { get; set; }

    /// <summary>First run / recreate — the workbook was created/seeded and nothing was pulled.</summary>
    public bool SeededOnly { get; set; }

    // Added rows are informational (shown as a status count only); only flag/purge warrant the dialog.
    private bool WorkbookChanged =>
        Update != null && (Update.Flagged.Count > 0 || Update.Purged.Count > 0);

    /// <summary>Whether the report dialog is worth showing.</summary>
    public bool HasReport =>
        !SeededOnly && ((SyncReport != null && SyncReport.HasIssues) || WorkbookChanged);

    /// <summary>One-line status for the VM status bar.</summary>
    public string StatusLine()
    {
        if (SeededOnly)
            return $"Workbook created — {Update.Added.Count} type(s) seeded.";
        if (SyncReport != null && SyncReport.Blocking)
            return SyncReport.StatusLine();

        var parts = new List<string>();
        if (SyncReport != null) parts.Add(SyncReport.StatusLine().TrimEnd('.'));
        parts.AddRange(WorkbookSummary());
        return string.Join("; ", parts) + ".";
    }

    /// <summary>Multi-line detail for the report dialog (empty when nothing to report).</summary>
    public string DetailBody()
    {
        var blocks = new List<string>();
        var sync = SyncReport?.DetailBody();
        if (!string.IsNullOrEmpty(sync)) blocks.Add(sync);

        void Section(string title, IReadOnlyList<string> items)
        {
            if (items == null || items.Count == 0) return;
            blocks.Add(title + ":\n" + string.Join("\n", items.Select(i => "  • " + i)));
        }
        // Added rows are surfaced as a status count only, not itemized in the dialog.
        Section("Flagged (missing — first cycle)", Update.Flagged);
        Section("Purged (still missing — row removed)", Update.Purged);

        return string.Join("\n\n", blocks).Trim();
    }

    private IEnumerable<string> WorkbookSummary()
    {
        var wb = new List<string>();
        if (Update.Added.Count > 0) wb.Add($"+{Update.Added.Count} new");
        if (Update.Flagged.Count > 0) wb.Add($"{Update.Flagged.Count} flagged");
        if (Update.Purged.Count > 0) wb.Add($"{Update.Purged.Count} purged");
        if (wb.Count > 0) yield return "workbook " + string.Join(", ", wb);
    }
}
