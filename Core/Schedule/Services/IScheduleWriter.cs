#nullable disable
using System.Collections.Generic;
using TurboSuite.Schedule.Models;

namespace TurboSuite.Schedule.Services;

/// <summary>One field to write on every symbol under a Type Mark group.</summary>
public class SpecFieldWrite
{
    public string Label { get; set; }
    public string ParamKey { get; set; }
    public bool IsBuiltIn { get; set; }
    public string Value { get; set; }
}

/// <summary>One dirty page's worth of writes, re-resolved by Type Mark + Kind at save time.</summary>
public class SpecWriteRequest
{
    public string TypeMark { get; set; }
    public PageKind Kind { get; set; }
    public List<SpecFieldWrite> Fields { get; set; } = new List<SpecFieldWrite>();
}

/// <summary>Outcome of a batched save.</summary>
public class ScheduleWriteResult
{
    /// <summary>Type Marks that had at least one field written.</summary>
    public int UpdatedTypes { get; set; }

    /// <summary>"Label on TypeMark" for fields whose write returned false (left dirty).</summary>
    public List<string> Skipped { get; set; } = new List<string>();

    /// <summary>ParamKey+TypeMark of fields that wrote successfully (so the VM can clear dirty).</summary>
    public HashSet<string> SavedKeys { get; set; } = new HashSet<string>();
}

/// <summary>
/// Revit-free contract for the batched writeback. Implemented shim-side (binding to the active
/// document) and invoked by <c>ScheduleMainViewModel</c> inside an <c>IRevitWorkQueue</c> work item,
/// so the whole save runs in one transaction on the Revit API thread.
/// </summary>
public interface IScheduleWriter
{
    ScheduleWriteResult Write(IReadOnlyList<SpecWriteRequest> pages);
}

/// <summary>Stable key for a (page, field) pair, shared by the VM and writer (net48 can't host
/// static interface members, so this lives on a plain class).</summary>
public static class ScheduleWriteKey
{
    public static string For(string typeMark, PageKind kind, string paramKey) =>
        $"{(int)kind}|{typeMark}|{paramKey}";
}
