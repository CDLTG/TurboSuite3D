#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using TurboSuite.Schedule.Models;

namespace TurboSuite.Schedule.Services;

/// <summary>
/// ClosedXML read/write for the per-project schedule workbook. Two sheets — <c>Fixtures</c> and
/// <c>Drivers</c> (sheet = <see cref="PageKind"/>) — plus a hidden <c>_meta</c> sheet. Layout is a
/// three-row header (section band / label / units), a frozen Type Mark column, and three collapsible
/// column outline groups (Catalog #, Catalog Qty, Notes).
///
/// <para><b>Add-only.</b> <see cref="WriteAddOnly"/> creates the workbook on first run and, on later runs,
/// only <i>appends</i> rows for Type Marks not already present — it never rewrites an existing
/// designer-authored cell. Type Marks that have vanished from the model are flagged, never deleted.</para>
///
/// <para><b>Read-safety.</b> <see cref="Read"/> maps columns by the row-2 header Label (not by position), so
/// outline grouping/reordering is irrelevant. n/a and ⟨varies⟩ cells are seeded <i>empty</i> (their state
/// shown by fill colour alone — dark grey = parameter missing, light grey = locked, amber = varies) so a
/// stale marker can never be mistaken for a value on Sync.</para>
/// </summary>
public static class ScheduleWorkbookIo
{
    private const string MetaSheet = "_meta";
    private const int BandRow = 1, LabelRow = 2, UnitRow = 3, FirstDataRow = 4;
    private const string TypeMarkHeader = "Type Mark";

    private static readonly (string Sheet, PageKind Kind)[] SheetMap =
    {
        ("Fixtures", PageKind.Fixture),
        ("Drivers", PageKind.Driver),
    };

    // Section band fills (light) + a dark header text band; matches the muted palette Counts uses.
    private static readonly Dictionary<SpecSection, XLColor> BandFill = new()
    {
        [SpecSection.Identity] = XLColor.FromHtml("#DDEBF7"),
        [SpecSection.Electrical] = XLColor.FromHtml("#FCE4D6"),
        [SpecSection.Mechanical] = XLColor.FromHtml("#E2EFDA"),
        [SpecSection.Photometric] = XLColor.FromHtml("#FFF2CC"),
        [SpecSection.Notes] = XLColor.FromHtml("#EDEDED"),
    };
    private static readonly XLColor LabelFill = XLColor.FromHtml("#262626");
    private static readonly XLColor LabelFont = XLColor.White;
    private static readonly XLColor LockedFill = XLColor.FromHtml("#F2F2F2");
    private static readonly XLColor NaFill = XLColor.FromHtml("#D9D9D9");
    private static readonly XLColor VariesFill = XLColor.FromHtml("#FFF2CC");
    private static readonly XLColor FlaggedFill = XLColor.FromHtml("#FFC7CE");

    // Short header aliases: the sheet shows the alias, but Read normalizes it back to the canonical
    // FieldDef.Label so the planner's header→field matching is unaffected. Owned entirely here.
    private static readonly Dictionary<string, string> HeaderAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Remote Power Supply"] = "RPS",
    };
    private static readonly Dictionary<string, string> HeaderCanonical =
        HeaderAlias.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    // Units row text, by field Label (display-only; Read ignores this row).
    private static readonly Dictionary<string, string> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Power"] = "W", ["Power/Length"] = "W/ft", ["Voltage"] = "V", ["Sub-Driver Power"] = "W",
        ["Derating Factor"] = "%", ["Amps Per Channel"] = "A", ["Maximum Fixtures"] = "count",
        ["DMX Channels"] = "ch", ["DMX Bundle Size"] = "ct", ["Lumens"] = "lm", ["Efficacy"] = "lm/W",
        ["Beam °"] = "°", ["CBCP"] = "cd", ["CCT"] = "K", ["Ceiling Thickness"] = "in",
    };

    // ── Column plan ─────────────────────────────────────────────────────────────────────

    /// <summary>Ordered field columns for a sheet (Type Mark is col 1, not in this list). De-interleaves
    /// the roster's Catalog #/Qty/Note pairs into three contiguous, independently-collapsible blocks.</summary>
    private static List<FieldDef> ColumnDefs(PageKind kind)
    {
        var applic = FieldDef.Roster.Where(d => d.AppliesTo(kind)).ToList();

        // Identity "normal" fields in roster order, but Model pulled to the front as the context column.
        var identity = applic.Where(d => d.Section == SpecSection.Identity && d.Role == FieldRole.Normal)
                             .OrderBy(d => d.Label == "Model" ? 0 : 1).ToList();
        var catNums = applic.Where(d => d.Role == FieldRole.CatalogNumber).OrderBy(d => d.Slot);
        var catQty = applic.Where(d => d.Role == FieldRole.CatalogQty).OrderBy(d => d.Slot);
        var elec = applic.Where(d => d.Section == SpecSection.Electrical);
        var mech = applic.Where(d => d.Section == SpecSection.Mechanical);
        var photo = applic.Where(d => d.Section == SpecSection.Photometric);
        var notes = applic.Where(d => d.Role == FieldRole.Note).OrderBy(d => d.Slot);

        var cols = new List<FieldDef>();
        cols.AddRange(identity);
        cols.AddRange(catNums);
        cols.AddRange(catQty);
        cols.AddRange(elec);
        cols.AddRange(mech);
        cols.AddRange(photo);
        cols.AddRange(notes);
        return cols;
    }

    // ── Write (add-only) ────────────────────────────────────────────────────────────────

    public static WorkbookUpdateResult WriteAddOnly(string path, IReadOnlyList<FixtureTypeSpec> currentModel, WorkbookMeta meta)
    {
        var result = new WorkbookUpdateResult();
        bool exists = File.Exists(path);
        using var wb = exists ? new XLWorkbook(path) : new XLWorkbook();

        foreach (var (sheetName, kind) in SheetMap)
        {
            var pages = currentModel.Where(p => p.Kind == kind)
                                    .ToDictionary(p => p.TypeMark.Trim(), p => p, StringComparer.OrdinalIgnoreCase);

            IXLWorksheet ws;
            if (!wb.TryGetWorksheet(sheetName, out ws))
                ws = BuildSheet(wb, sheetName, kind);

            var colByLabel = HeaderColumns(ws);
            int tmCol = colByLabel.TryGetValue(TypeMarkHeader, out var tc) ? tc : 1;

            // Existing rows: Type Mark → row index.
            var existingRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int lastRow = ws.LastRowUsed()?.RowNumber() ?? (FirstDataRow - 1);
            for (int r = FirstDataRow; r <= lastRow; r++)
            {
                var tm = ws.Cell(r, tmCol).GetString().Trim();
                if (tm.Length > 0 && !existingRows.ContainsKey(tm)) existingRows[tm] = r;
            }

            // Append new Type Marks.
            int appendRow = Math.Max(lastRow + 1, FirstDataRow);
            foreach (var tm in pages.Keys.Where(k => !existingRows.ContainsKey(k))
                                         .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                WriteDataRow(ws, appendRow, tmCol, colByLabel, pages[tm]);
                result.Added.Add($"{tm} ({kind})");
                appendRow++;
            }

            // One-cycle grace for removed types. Each existing row, re-evaluated every run:
            //   present            → clear any stale red (live again)
            //   missing, not red   → flag red (first cycle — a last warning)
            //   missing, already red → delete the row (still gone after its grace cycle)
            // The Type Mark cell is tool-owned context, never designer spec data. Deletes are gathered and
            // applied bottom-up so earlier row indices stay valid.
            var toPurge = new List<(int Row, string Tm)>();
            foreach (var kv in existingRows)
            {
                var tmCell = ws.Cell(kv.Value, tmCol);
                if (pages.ContainsKey(kv.Key))
                {
                    tmCell.Style.Fill.BackgroundColor = LockedFill;
                }
                else if (IsRedFill(tmCell))
                {
                    toPurge.Add((kv.Value, kv.Key));
                }
                else
                {
                    tmCell.Style.Fill.BackgroundColor = FlaggedFill;
                    result.Flagged.Add($"{kv.Key} ({kind})");
                }
            }
            foreach (var (row, tm) in toPurge.OrderByDescending(x => x.Row))
            {
                ws.Row(row).Delete();
                result.Purged.Add($"{tm} ({kind})");
            }
        }

        WriteMeta(wb, meta);
        wb.SaveAs(path);
        return result;
    }

    private static void WriteDataRow(IXLWorksheet ws, int row, int tmCol,
        Dictionary<string, int> colByLabel, FixtureTypeSpec page)
    {
        var tmCell = ws.Cell(row, tmCol);
        tmCell.Value = page.TypeMark;
        tmCell.Style.Fill.BackgroundColor = LockedFill;
        tmCell.Style.Font.Bold = true;
        tmCell.Style.Protection.SetLocked(true);

        var byLabel = page.AllFields.ToDictionary(f => f.Label, f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var kv in colByLabel)
        {
            if (kv.Key == TypeMarkHeader) continue;
            if (!byLabel.TryGetValue(kv.Key, out var f)) continue;
            var cell = ws.Cell(row, kv.Value);
            SeedFieldCell(cell, f);
        }
    }

    /// <summary>Seed one field's cell: value + lock/format by its state. n/a and ⟨varies⟩ are left
    /// <b>empty</b> (state shown via fill+comment) so Read never mistakes a marker for a value.</summary>
    private static void SeedFieldCell(IXLCell cell, SpecField f)
    {
        if (f.IsNa)
        {
            // Dark grey = parameter missing (not present on all types under this Type Mark).
            cell.Style.Fill.BackgroundColor = NaFill;
            cell.Style.Protection.SetLocked(true);
            return;
        }
        if (f.IsReadOnly)
        {
            // Light grey = parameter locked (formula/computed — edit in Revit).
            cell.Value = ReadonlyDisplay(f);
            cell.Style.Fill.BackgroundColor = LockedFill;
            cell.Style.Protection.SetLocked(true);
            return;
        }
        if (f.IsVaries)
        {
            // Amber = differs across instances (leave blank to keep, or type to unify).
            cell.Style.Fill.BackgroundColor = VariesFill;
            cell.Style.Protection.SetLocked(false);
            return;
        }

        // Editable, agreed value.
        cell.Style.Protection.SetLocked(false);
        switch (f.ValueKind)
        {
            case SpecValueKind.Boolean:
                cell.Value = ScheduleSyncPlanner.BoolToYesNo(f.OriginalValue);
                break;
            case SpecValueKind.Numeric:
                cell.Value = SpecNumericText.SeedCell(f.OriginalValue);
                break;
            default:
                cell.Value = f.OriginalValue ?? "";
                break;
        }
    }

    private static string ReadonlyDisplay(SpecField f) =>
        f.ValueKind == SpecValueKind.Boolean ? ScheduleSyncPlanner.BoolToYesNo(f.OriginalValue)
        : f.ValueKind == SpecValueKind.Numeric ? SpecNumericText.SeedCell(f.OriginalValue)
        : f.OriginalValue ?? "";

    // ── Sheet structure ─────────────────────────────────────────────────────────────────

    private static IXLWorksheet BuildSheet(XLWorkbook wb, string sheetName, PageKind kind)
    {
        var ws = wb.Worksheets.Add(sheetName);
        var cols = ColumnDefs(kind);

        // Col 1: Type Mark key.
        ws.Cell(LabelRow, 1).Value = TypeMarkHeader;
        ws.Column(1).Width = 12;

        // Field columns from col 2.
        int c = 2;
        var colIndex = new Dictionary<FieldDef, int>();
        foreach (var d in cols)
        {
            ws.Cell(LabelRow, c).Value = HeaderAlias.TryGetValue(d.Label, out var alias) ? alias : d.Label;
            if (Units.TryGetValue(d.Label, out var u)) ws.Cell(UnitRow, c).Value = u;
            ws.Column(c).Width = ColumnWidth(d);
            colIndex[d] = c;
            c++;
        }
        int lastCol = c - 1;

        StyleHeader(ws, cols, colIndex, lastCol);
        ApplyOutlineGroups(ws, cols, colIndex);
        ApplyDropdowns(ws, cols, colIndex);

        ws.SheetView.FreezeRows(UnitRow);   // band + label + units frozen
        ws.SheetView.FreezeColumns(1);      // Type Mark frozen

        // Everything is locked by default under protection; data cells are unlocked as they're written.
        ws.Protect().AllowElement(XLSheetProtectionElements.FormatColumns)
                    .AllowElement(XLSheetProtectionElements.FormatRows);
        return ws;
    }

    private static void StyleHeader(IXLWorksheet ws, List<FieldDef> cols, Dictionary<FieldDef, int> colIndex, int lastCol)
    {
        // Section bands (row 1), merged over each section's contiguous column span.
        // Identity band also covers the Type Mark key column (col 1).
        void Band(SpecSection section, int from, int to, string text)
        {
            var range = ws.Range(BandRow, from, BandRow, to);
            range.Merge();
            range.Value = text;
            range.Style.Fill.BackgroundColor = BandFill[section];
            range.Style.Font.Bold = true;
            range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        // Group contiguous columns by section, in visual order.
        int start = 1; // Type Mark
        SpecSection curr = SpecSection.Identity;
        int spanFrom = 1;
        var ordered = cols.Select(d => (Section: d.Section, Col: colIndex[d])).ToList();
        // Walk the field columns; Type Mark (col 1) belongs to the first (Identity) band.
        foreach (var (section, col) in ordered)
        {
            if (section != curr)
            {
                Band(curr, spanFrom, col - 1, curr.ToString().ToUpperInvariant());
                curr = section;
                spanFrom = col;
            }
        }
        Band(curr, spanFrom, lastCol, curr.ToString().ToUpperInvariant());

        // Label row.
        var labels = ws.Range(LabelRow, 1, LabelRow, lastCol);
        labels.Style.Fill.BackgroundColor = LabelFill;
        labels.Style.Font.FontColor = LabelFont;
        labels.Style.Font.Bold = true;
        labels.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        // Units row.
        var units = ws.Range(UnitRow, 1, UnitRow, lastCol);
        units.Style.Font.Italic = true;
        units.Style.Font.FontColor = XLColor.FromHtml("#808080");
        units.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        _ = start; // (Type Mark band start reference kept for clarity)
    }

    private static void ApplyOutlineGroups(IXLWorksheet ws, List<FieldDef> cols, Dictionary<FieldDef, int> colIndex)
    {
        ws.Outline.SummaryHLocation = XLOutlineSummaryHLocation.Left;

        // Catalog #: keep #1 visible, collapse #2–#6.
        GroupSlots(ws, cols, colIndex, FieldRole.CatalogNumber, keepFirst: true);
        // Catalog Qty: collapse all.
        GroupSlots(ws, cols, colIndex, FieldRole.CatalogQty, keepFirst: false);
        // Notes: keep Note 1 visible, collapse 2–6.
        GroupSlots(ws, cols, colIndex, FieldRole.Note, keepFirst: true);
    }

    private static void GroupSlots(IXLWorksheet ws, List<FieldDef> cols, Dictionary<FieldDef, int> colIndex,
        FieldRole role, bool keepFirst)
    {
        var slots = cols.Where(d => d.Role == role).OrderBy(d => d.Slot).ToList();
        if (slots.Count == 0) return;
        var group = keepFirst ? slots.Skip(1).ToList() : slots;
        if (group.Count == 0) return;

        int from = group.Min(d => colIndex[d]);
        int to = group.Max(d => colIndex[d]);
        ws.Columns(from, to).Group();
        ws.Columns(from, to).Collapse();
    }

    private static void ApplyDropdowns(IXLWorksheet ws, List<FieldDef> cols, Dictionary<FieldDef, int> colIndex)
    {
        // Yes/No for boolean-authored fields — identified by param, but we can't see StorageType here, so we
        // apply the Yes/No list to the known boolean field (Remote Power Supply) plus Dimming Protocol enum.
        void ListValidation(int col, string listExpr)
        {
            var range = ws.Range(FirstDataRow, col, FirstDataRow + 5000, col);
            var dv = range.CreateDataValidation();
            dv.List(listExpr, true);
            dv.IgnoreBlanks = true;
        }

        foreach (var d in cols)
        {
            if (d.Label == "Remote Power Supply")
                ListValidation(colIndex[d], "\"Yes,No\"");
            else if (d.Label == "Dimming Protocol")
                ListValidation(colIndex[d], "\"0-10V,ELV,MLV,DMX,DALI,WIFI,Line Voltage,PWM\"");
        }
    }

    private static double ColumnWidth(FieldDef d)
    {
        if (d.Label == "Remote Power Supply") return 8;        // RPS — narrow Yes/No
        if (d.Role == FieldRole.CatalogNumber) return 18;      // Catalog #1–#6
        if (d.Role == FieldRole.CatalogQty) return 10;         // Catalog Qty1–6
        return d.Section switch
        {
            SpecSection.Notes => 30,
            SpecSection.Electrical => 16,
            SpecSection.Mechanical => 16,
            SpecSection.Identity => 18,                         // Model + identity text
            _ => 12,                                            // Photometric and anything else
        };
    }

    // ── Meta ────────────────────────────────────────────────────────────────────────────

    private static void WriteMeta(XLWorkbook wb, WorkbookMeta meta)
    {
        if (!wb.TryGetWorksheet(MetaSheet, out var ws))
            ws = wb.Worksheets.Add(MetaSheet);
        ws.Cell("A1").Value = "ProjectPath"; ws.Cell("B1").Value = meta.ProjectPath ?? "";
        ws.Cell("A2").Value = "RevitVersion"; ws.Cell("B2").Value = meta.RevitVersion ?? "";
        ws.Cell("A3").Value = "LastUpdated"; ws.Cell("B3").Value = meta.LastUpdated ?? "";
        ws.Hide();
    }

    // ── Read ────────────────────────────────────────────────────────────────────────────

    public static WorkbookSnapshot Read(string path)
    {
        var snapshot = new WorkbookSnapshot();
        using var wb = new XLWorkbook(path);

        snapshot.Meta = ReadMeta(wb);

        foreach (var (sheetName, kind) in SheetMap)
        {
            if (!wb.TryGetWorksheet(sheetName, out var ws)) continue;

            var colByLabel = HeaderColumns(ws);
            if (!colByLabel.TryGetValue(TypeMarkHeader, out var tmCol)) continue;

            var sheet = new WorkbookSheet { Kind = kind };
            int lastRow = ws.LastRowUsed()?.RowNumber() ?? (FirstDataRow - 1);
            for (int r = FirstDataRow; r <= lastRow; r++)
            {
                var tm = ws.Cell(r, tmCol).GetString().Trim();
                if (tm.Length == 0) continue;

                var row = new WorkbookRow { TypeMark = tm };
                foreach (var kv in colByLabel)
                {
                    if (kv.Key == TypeMarkHeader) continue;
                    row.Cells[kv.Key] = ws.Cell(r, kv.Value).GetString();
                }
                sheet.Rows.Add(row);
            }
            snapshot.Sheets.Add(sheet);
        }
        return snapshot;
    }

    private static WorkbookMeta ReadMeta(XLWorkbook wb)
    {
        var meta = new WorkbookMeta();
        if (!wb.TryGetWorksheet(MetaSheet, out var ws)) return meta;
        for (int r = 1; r <= 10; r++)
        {
            var key = ws.Cell(r, 1).GetString().Trim();
            var val = ws.Cell(r, 2).GetString();
            switch (key)
            {
                case "ProjectPath": meta.ProjectPath = val; break;
                case "RevitVersion": meta.RevitVersion = val; break;
                case "LastUpdated": meta.LastUpdated = val; break;
            }
        }
        return meta;
    }

    /// <summary>Row-2 header Label → column index (the resilient matching key). Skips blank headers.</summary>
    private static Dictionary<string, int> HeaderColumns(IXLWorksheet ws)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 1;
        for (int c = 1; c <= lastCol; c++)
        {
            var label = ws.Cell(LabelRow, c).GetString().Trim();
            if (label.Length == 0) continue;
            if (HeaderCanonical.TryGetValue(label, out var canon)) label = canon; // "RPS" → "Remote Power Supply"
            if (!map.ContainsKey(label)) map[label] = c;
        }
        return map;
    }

    /// <summary>True when a cell carries the removed-type red fill (<see cref="FlaggedFill"/> = #FFC7CE) —
    /// the persisted "flagged last cycle" tombstone. Safe against theme/indexed/no-fill cells.</summary>
    private static bool IsRedFill(IXLCell cell)
    {
        var fill = cell.Style.Fill.BackgroundColor;
        if (fill == null || fill.ColorType != XLColorType.Color) return false;
        var c = fill.Color;
        return c.R == 0xFF && c.G == 0xC7 && c.B == 0xCE;
    }
}
