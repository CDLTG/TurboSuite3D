#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Docs.Services;      // Catalog #/Qty validators (warn-still-write)
using TurboSuite.Schedule.Models;

namespace TurboSuite.Schedule.Services;

/// <summary>
/// Pure workbook→model diff. Given a <see cref="WorkbookSnapshot"/> (as read from the .xlsx) and the
/// current-model pages (as the collector reconciles them), it produces the <see cref="SpecWriteRequest"/>s
/// for genuinely-changed cells plus a <see cref="SyncReport"/> of everything it couldn't/wouldn't write.
/// No Revit types — the shim gateway feeds it the collected pages and hands the requests to the writer.
///
/// <para>The load-bearing invariant is <b>no spurious writes</b>: an untouched cell must key equal to the
/// model value it was seeded from, across every ValueKind. Numbers go through <see cref="SpecNumericText"/>;
/// booleans through <see cref="NormalizeBool"/>; text is trimmed exact.</para>
/// </summary>
public static class ScheduleSyncPlanner
{
    /// <summary>Intentional-empty sentinel for a String field (blank = skip, so this is the only way to
    /// clear one from Excel). Case-insensitive.</summary>
    public const string ClearSentinel = "<clear>";

    public static (List<SpecWriteRequest> Requests, SyncReport Report) Plan(
        WorkbookSnapshot snapshot, IReadOnlyList<FixtureTypeSpec> currentModel)
    {
        var report = new SyncReport();
        var requests = new List<SpecWriteRequest>();

        foreach (var sheet in snapshot.Sheets)
        {
            var kind = sheet.Kind;

            // Model pages of this Kind, keyed by trimmed Type Mark (collector already made these unique).
            var modelByMark = currentModel
                .Where(p => p.Kind == kind)
                .ToDictionary(p => p.TypeMark.Trim(), p => p, StringComparer.OrdinalIgnoreCase);

            // Header Label → roster field applicable to this Kind. Labels are unique across the roster.
            var labelToDef = FieldDef.Roster
                .Where(d => d.AppliesTo(kind))
                .ToDictionary(d => d.Label, d => d, StringComparer.OrdinalIgnoreCase);

            // Duplicate Type Mark within a sheet is a blocking, resolve-by-hand error.
            foreach (var dup in sheet.Rows
                         .Where(r => !string.IsNullOrWhiteSpace(r.TypeMark))
                         .GroupBy(r => r.TypeMark.Trim(), StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
            {
                report.Blocking = true;
                report.Errors.Add($"'{dup.Key}' appears {dup.Count()} times on the {kind} sheet — remove the duplicate row(s).");
            }

            foreach (var row in sheet.Rows)
            {
                var tm = (row.TypeMark ?? "").Trim();
                if (tm.Length == 0) continue; // blank spacer row

                // No such placed type — silently ignored. Removals are surfaced by the workbook flag/purge,
                // not here (this would only otherwise flag a phantom row a designer typed, which is rare).
                if (!modelByMark.TryGetValue(tm, out var page))
                    continue;

                var fieldByLabel = page.AllFields.ToDictionary(f => f.Label, f => f, StringComparer.OrdinalIgnoreCase);
                var req = new SpecWriteRequest { TypeMark = page.TypeMark, Kind = kind };

                foreach (var cell in row.Cells)
                {
                    var label = cell.Key;
                    var raw = cell.Value ?? "";

                    if (!labelToDef.TryGetValue(label, out var def)) continue; // unknown header → ignore silently
                    if (!fieldByLabel.TryGetValue(label, out var field)) continue;

                    var (action, value) = EvaluateCell(field, raw);

                    if (field.IsNa || field.IsReadOnly)
                    {
                        // Locked cell: surface it only if the designer actually attempted a change (an
                        // override of the greyed value / junk in a locked cell). Untouched → silent.
                        if (action != CellAction.NoChange)
                            report.Skipped.Add($"{label} on {tm} ({(field.IsNa ? "n/a" : "read-only")})");
                        continue;
                    }

                    switch (action)
                    {
                        case CellAction.Write:
                            WarnCatalog(def, value, tm, report);
                            req.Fields.Add(Write(def, value));
                            break;
                        case CellAction.Unparsable:
                            report.Skipped.Add($"{label} on {tm} (unrecognized Yes/No: '{raw.Trim()}')");
                            break;
                        // CellAction.NoChange → nothing
                    }
                }

                if (req.Fields.Count > 0)
                {
                    requests.Add(req);
                    report.ChangedTypes++;
                    report.ChangedFields += req.Fields.Count;
                }
            }
        }

        // A blocking sheet poisons the whole Sync — emit nothing.
        if (report.Blocking)
        {
            report.ChangedTypes = 0;
            report.ChangedFields = 0;
            return (new List<SpecWriteRequest>(), report);
        }

        return (requests, report);
    }

    private enum CellAction { NoChange, Write, Unparsable }

    /// <summary>Classify one cell against its reconciled model field, with no side effects — so the caller
    /// can decide whether to write it (editable) or merely report an attempted override (locked). Blank =
    /// no change; <c>&lt;clear&gt;</c> empties a String; numbers compare number-tolerant; Yes/No junk =
    /// Unparsable.</summary>
    private static (CellAction Action, string Value) EvaluateCell(SpecField field, string raw)
    {
        var trimmed = (raw ?? "").Trim();

        switch (field.ValueKind)
        {
            case SpecValueKind.Text:
            {
                string newValue;
                if (string.Equals(trimmed, ClearSentinel, StringComparison.OrdinalIgnoreCase))
                    newValue = "";                       // intentional clear
                else if (trimmed.Length == 0)
                    return (CellAction.NoChange, null);  // blank = skip (no change)
                else
                    newValue = trimmed;

                return newValue == (field.OriginalValue ?? "").Trim()
                    ? (CellAction.NoChange, null)
                    : (CellAction.Write, newValue);
            }

            case SpecValueKind.Boolean:
            {
                if (trimmed.Length == 0) return (CellAction.NoChange, null);
                if (!TryNormalizeBool(trimmed, out var norm)) return (CellAction.Unparsable, trimmed);
                TryNormalizeBool(field.OriginalValue, out var cur); // "1"/"0" → same normalizer
                return norm == cur ? (CellAction.NoChange, null) : (CellAction.Write, norm);
            }

            default: // Numeric
            {
                if (trimmed.Length == 0) return (CellAction.NoChange, null);
                return SpecNumericText.CompareKey(trimmed) == SpecNumericText.CompareKey(field.OriginalValue)
                    ? (CellAction.NoChange, null)        // unchanged (number-tolerant / verbatim-length)
                    : (CellAction.Write, trimmed);       // writer SetValueString re-parses against units
            }
        }
    }

    private static SpecFieldWrite Write(FieldDef def, string value) => new SpecFieldWrite
    {
        Label = def.Label,
        ParamKey = def.ParamKey,
        IsBuiltIn = def.IsBuiltIn,
        Value = value
    };

    /// <summary>Catalog #/Qty tokens are validated but never blocking — a bad token warns and still writes,
    /// matching Counts's lenient authoring stance.</summary>
    private static void WarnCatalog(FieldDef def, string value, string tm, SyncReport report)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            if (def.Role == FieldRole.CatalogNumber) CatalogLengthTokenResolver.Validate(value);
            else if (def.Role == FieldRole.CatalogQty) CatalogQtyParser.Parse(value);
        }
        catch (Exception ex)
        {
            report.Warnings.Add($"{def.Label} on {tm}: {ex.Message} (written anyway)");
        }
    }

    private static readonly HashSet<string> TrueWords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1", "yes", "y", "true", "t", "x", "on", "checked", "✓" };
    private static readonly HashSet<string> FalseWords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0", "no", "n", "false", "f", "off", "unchecked", "" };

    /// <summary>Normalize a Yes/No-ish token to "1"/"0". Returns false for anything unrecognized.</summary>
    public static bool TryNormalizeBool(string raw, out string normalized)
    {
        var s = (raw ?? "").Trim();
        if (TrueWords.Contains(s)) { normalized = "1"; return true; }
        if (FalseWords.Contains(s)) { normalized = "0"; return true; }
        normalized = null;
        return false;
    }

    /// <summary>Convenience: "1"/"0" → "Yes"/"No" for the export cell.</summary>
    public static string BoolToYesNo(string stored) =>
        TryNormalizeBool(stored, out var n) && n == "1" ? "Yes" : "No";
}
