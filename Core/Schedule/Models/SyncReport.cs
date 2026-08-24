#nullable disable
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Schedule.Models;

/// <summary>
/// The best-effort outcome of a Sync (file → model). The planner fills the diagnostic buckets; the shim
/// gateway merges the writer's post-write skips into <see cref="WriterSkipped"/>. When
/// <see cref="Blocking"/> is set (a duplicate Type Mark within a sheet) the planner emits <b>no</b> writes
/// at all — the user must fix the workbook and re-Sync.
/// </summary>
public class SyncReport
{
    /// <summary>A structural problem that aborted the whole Sync (nothing was written).</summary>
    public bool Blocking { get; set; }

    /// <summary>Blocking reasons (duplicate Type Marks) — human-readable.</summary>
    public List<string> Errors { get; set; } = new List<string>();

    /// <summary>"Label on TypeMark" for cells the designer actually attempted to edit but couldn't apply:
    /// an override of a read-only / n/a cell, or junk in a Yes/No cell. Unchanged locked cells stay silent.</summary>
    public List<string> Skipped { get; set; } = new List<string>();

    /// <summary>Non-blocking warnings (bad Catalog #/Qty token) — the value is still written.</summary>
    public List<string> Warnings { get; set; } = new List<string>();

    /// <summary>Fields the writer itself could not apply (read-only at write time / SetValueString false).</summary>
    public List<string> WriterSkipped { get; set; } = new List<string>();

    /// <summary>Types with at least one field queued for write.</summary>
    public int ChangedTypes { get; set; }

    /// <summary>Total fields queued for write.</summary>
    public int ChangedFields { get; set; }

    public bool HasIssues =>
        Errors.Count > 0 || Skipped.Count > 0 || Warnings.Count > 0 || WriterSkipped.Count > 0;

    /// <summary>One-line status suitable for the VM status bar.</summary>
    public string StatusLine()
    {
        if (Blocking)
            return $"Sync blocked: {string.Join("; ", Errors)}";

        var parts = new List<string> { $"Updated {ChangedTypes} type(s), {ChangedFields} field(s)" };
        if (Skipped.Count > 0) parts.Add($"{Skipped.Count} skipped");
        if (Warnings.Count > 0) parts.Add($"{Warnings.Count} warning(s)");
        if (WriterSkipped.Count > 0) parts.Add($"{WriterSkipped.Count} not applied");
        return string.Join("; ", parts) + ".";
    }

    /// <summary>Multi-line detail body for the report dialog (empty when nothing to report).</summary>
    public string DetailBody()
    {
        var sb = new List<string>();
        void Section(string title, IReadOnlyList<string> items)
        {
            if (items.Count == 0) return;
            sb.Add(title + ":");
            sb.AddRange(items.Select(i => "  • " + i));
            sb.Add("");
        }

        Section("Blocking errors", Errors);
        Section("Skipped cells (locked-cell override / unrecognized)", Skipped);
        Section("Warnings (written anyway)", Warnings);
        Section("Not applied by writer", WriterSkipped);
        return string.Join("\n", sb).TrimEnd();
    }
}
