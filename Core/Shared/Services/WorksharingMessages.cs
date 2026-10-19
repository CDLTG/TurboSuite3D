using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Shared.Services;

/// <summary>
/// The user-facing worksharing messages, extracted Revit-free so their wording is pinned by unit tests and
/// shared by shim services and Core ViewModels alike. <see cref="BuildDenial"/> is the one message a refused
/// lease shows: the shim (<c>WorksharingLease</c>) classifies each element it failed to check out and hands
/// the tallies here.
///
/// A lease is all-or-nothing: the operation either holds its whole working set or runs nothing, so the
/// message only has to say <b>who to ask</b> and <b>what to do</b>. Three causes, in order of how the user
/// resolves them:
/// <list type="bullet">
/// <item>a teammate holds the module lock — they have the module open (or had it, and haven't synced);</item>
/// <item>a teammate holds some of the elements — they're editing them and haven't synced;</item>
/// <item>no owner — the local copy is behind central (a teammate synced a change), so Reload Latest.</item>
/// </list>
/// </summary>
public static class WorksharingMessages
{
    public const string Unreachable =
        "Can't reach the central model. Check your connection, then try again.";

    /// <summary>A write transaction came back rolled back (the <c>CommitGuard</c> backstop).</summary>
    public const string RolledBack = "Revit rolled back the change — nothing was saved.";

    /// <param name="module">Display name of the module being leased (e.g. "TurboSchedule"); only used
    /// when <paramref name="lockOwners"/> is non-empty.</param>
    /// <param name="lockOwners">Users holding the module's lock store.</param>
    /// <param name="itemsByOwner">Count of working-set elements held per other user.</param>
    /// <param name="staleCount">Elements refused with no owner — local is behind central.</param>
    /// <param name="itemNoun">Singular noun for the elements ("lighting type", "circuit"…).</param>
    public static string BuildDenial(
        string module,
        IReadOnlyCollection<string>? lockOwners,
        IReadOnlyDictionary<string, int>? itemsByOwner,
        int staleCount,
        string? itemNoun = "item")
    {
        lockOwners ??= new string[0];
        itemsByOwner ??= new Dictionary<string, int>();
        string noun = string.IsNullOrWhiteSpace(itemNoun) ? "item" : itemNoun!;

        var lines = new List<string>();

        var lockers = lockOwners.Where(o => !string.IsNullOrWhiteSpace(o))
            .Distinct().OrderBy(o => o).ToList();
        if (lockers.Count > 0)
            lines.Add($"{JoinNames(lockers)} {(lockers.Count == 1 ? "is" : "are")} working in {module} " +
                      "— ask them to sync, then try again.");

        // An owner already named for the lock is holding the elements for the same reason; don't repeat them.
        var holders = itemsByOwner
            .Where(kv => kv.Value > 0 && !string.IsNullOrWhiteSpace(kv.Key) && !lockers.Contains(kv.Key))
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .ToList();
        if (holders.Count > 0)
        {
            var parts = holders.Select((kv, i) => i == 0
                ? $"{Count(kv.Value, noun)} {(kv.Value == 1 ? "is" : "are")} being edited by {kv.Key}"
                : $"{kv.Value} by {kv.Key}");
            lines.Add(string.Join(", ", parts) + " — ask them to sync, then try again.");
        }

        if (staleCount > 0)
            lines.Add(lines.Count == 0
                ? "The model has newer changes from central — Reload Latest, then try again."
                : "The model also has newer changes from central — Reload Latest once they've synced.");

        if (lines.Count == 0)
            lines.Add("Some elements couldn't be borrowed from the central model. Sync, then try again.");

        return string.Join("\n\n", lines);
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string JoinNames(IReadOnlyList<string> names) =>
        names.Count switch
        {
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => string.Join(", ", names.Take(names.Count - 1)) + ", and " + names[names.Count - 1],
        };
}
