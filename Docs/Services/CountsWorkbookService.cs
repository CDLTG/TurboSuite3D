using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using ClosedXML.Excel;
using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

public static class CountsWorkbookService
{
    #region Constants

    // Worksheet column indices (1-based)
    private const int WsColType = 1;        // A
    private const int WsColMfr = 2;         // B
    private const int WsColCatalog = 3;     // C
    private const int WsColQty = 4;         // D
    private const int WsColPrevQty = 5;     // E
    private const int WsColDelta = 6;       // F
    // Visual separator between the locked Revit-side block (A–F) and the editable
    // pricing block (H–V). Mirrors the Type value in gray italic; locked.
    private const int WsColTypeRepeat = 7;  // G
    private const int WsColDesc = 8;        // H
    private const int WsColCalc = 9;        // I
    private const int WsColUnitCost = 10;   // J
    private const int WsColMarkup = 11;     // K
    private const int WsColTariff = 12;     // L
    private const int WsColAdder = 13;      // M
    private const int WsColPhase = 14;      // N
    // Per-Type Schedule Notes — canonical literal on each type's first row (unlocked),
    // gray-italic "---" placeholder on subsequent rows (locked). Seeded once from Revit on
    // a type's first creation and user-owned thereafter (updates never overwrite).
    private const int WsColNote1 = 15;      // O
    private const int WsColNote2 = 16;      // P
    private const int WsColNote3 = 17;      // Q
    private const int WsColNote4 = 18;      // R
    private const int WsColNote5 = 19;      // S
    private const int WsColNote6 = 20;      // T
    private const int WsColMfrOverride = 21;  // U — user-editable, overrides B
    private const int WsColQtyOverride = 22;  // V — user-editable, overrides D
    // Effective Qty lives inside the hidden helper block (BR) rather than adjacent to the
    // editable columns — keeps the visible worksheet clean and avoids a visually-empty slot
    // between V and the end of the visible data. =IF(V="",D,V) written per-row.
    private const int WsColEffQty      = 70;  // BR — hidden helper

    // Hidden helper pipeline columns on Worksheet. Active flag (AF) is a per-row 0/1 literal
    // written by C#; every helper spill formula filters on (AF=1) to exclude strikethrough rows.
    // AG-AP feed the Quote sheet; AQ-AY / AZ-BH / BI-BP feed Phase 1/2/3. The final column of
    // each set (AP/AY/BH/BR) is an InDataBlock flag (1 for data/tariff/note/gap rows, blank for
    // footer/notes-library) that drives the print-sheet border CF. All columns AF and beyond
    // are hidden and locked.
    private const int WsColActive = 33;     // AG
    private const string HelperFirstCol = "AH";
    private const int WsColHelperLast = 71; // BS (Phase 3 InDataBlock flag)

    private static readonly string[] QuoteHelperCols =   { "AH", "AI", "AJ", "AK", "AL", "AM", "AN", "AO", "AP", "AQ" };
    private static readonly string[] Phase1HelperCols =  { "AR", "AS", "AT", "AU", "AV", "AW", "AX", "AY", "AZ" };
    private static readonly string[] Phase2HelperCols =  { "BA", "BB", "BC", "BD", "BE", "BF", "BG", "BH", "BI" };
    private static readonly string[] Phase3HelperCols =  { "BJ", "BK", "BL", "BM", "BN", "BO", "BP", "BQ", "BS" };

    // Counts sheet column indices (1-based)
    private const int CsColType = 1;        // A
    private const int CsColMfr = 2;         // B
    private const int CsColCat1 = 3;        // C
    private const int CsColCat2 = 4;        // D
    private const int CsColCat3 = 5;        // E
    private const int CsColCat4 = 6;        // F
    private const int CsColCat5 = 7;        // G
    private const int CsColCat6 = 8;        // H
    private const int CsColCount = 9;       // I
    private const int CsColLinear = 10;     // J
    private const int CsColReel = 11;       // K
    private const int CsColChannel = 12;    // L
    private const int CsColNote1 = 13;      // M — Schedule Notes 1..6 emitted for reference
    private const int CsColNote6 = 18;      // R
    // Hidden helper column — Type|Cat1Cat2…Cat6 concatenation used by Worksheet col E's
    // SUMIFS lookup when a Reference Counts baseline is selected on the Dashboard.
    private const int CsColCatCombo = 19;   // S

    // Highlight colors
    private static readonly XLColor GreenFill = XLColor.FromHtml("#C6EFCE");
    private static readonly XLColor RedFill = XLColor.FromHtml("#FFC7CE");
    private static readonly XLColor YellowFill = XLColor.FromHtml("#FFEB9C");

    #endregion

    /// <summary>
    /// Creates a new Counts workbook. Rep directory path comes from TurboDocs user settings
    /// (CountsViewModel passes it); seeds Dashboard!B3 and is used to build the Rep Lists sheet.
    /// Descriptions and pricing are no longer auto-filled — pricing team enters them manually.
    /// </summary>
    public static void GenerateNew(
        List<CountsFixtureModel> fixtures,
        string projectName,
        string projectLocation,
        string outputPath,
        string repDirectoryPath,
        string headerImagePath = "",
        string footerImagePath = "")
    {
        using var wb = new XLWorkbook();

        string dateString = DateTime.Now.ToString("yyyy.MM.dd");
        string countsSheetName = $"Counts {dateString}";

        var repDirectory = ReadRepDirectory(repDirectoryPath);

        BuildCoverSheet(wb, projectName, projectLocation);
        BuildDashboardSheet(wb, projectName, repDirectoryPath);
        BuildWorksheetSheet(wb, fixtures, countsSheetName, null);
        BuildRepListsSheet(wb, fixtures, repDirectory);
        BuildQuoteSheet(wb);
        for (int p = 1; p <= 3; p++)
            BuildPhaseQuoteSheet(wb, p);
        BuildChangesSheet(wb);
        BuildCountsSheet(wb, fixtures, countsSheetName);

        wb.SaveAs(outputPath);

        var spillSheets = new List<(string sheetName, int? minColumn)>
        {
            ("Worksheet", WsColActive),
            ("Quote", null),
            ("Phase 1", null),
            ("Phase 2", null),
            ("Phase 3", null),
        };
        PatchDynamicArrayMetadata(outputPath, spillSheets);
        EmbedHeaderFooterImages(outputPath, headerImagePath, footerImagePath,
            new[] { "Quote", "Phase 1", "Phase 2", "Phase 3" });
    }

    /// <summary>
    /// Opens an existing workbook and updates it with fresh Revit data.
    /// </summary>
    public static void GenerateUpdate(
        List<CountsFixtureModel> fixtures,
        string existingPath,
        string repDirectoryPath,
        string headerImagePath = "",
        string footerImagePath = "")
    {
        using var wb = new XLWorkbook(existingPath);

        string dateString = DateTime.Now.ToString("yyyy.MM.dd");
        string countsSheetName = ResolveCountsSheetName(wb, dateString);

        // Find previous Counts sheet for change detection
        var prevCountsSheet = FindLatestCountsSheet(wb);
        var prevData = prevCountsSheet != null ? ReadCountsSheetData(prevCountsSheet) : null;

        // Migrate legacy workbooks (no Dashboard) and seed/keep Rep Directory path.
        EnsureDashboardSheet(wb, repDirectoryPath);

        // Dashboard!B3 is authoritative; fall back to settings only when empty.
        string effectiveRepPath = ReadRepDirectoryPathFromDashboard(wb);
        if (string.IsNullOrWhiteSpace(effectiveRepPath))
            effectiveRepPath = repDirectoryPath;
        var repDirectory = ReadRepDirectory(effectiveRepPath);

        // Read existing Worksheet rows
        var existingRows = ReadExistingWorksheetRows(wb);

        // Build new Counts sheet
        BuildCountsSheet(wb, fixtures, countsSheetName);

        // Refresh Reference Counts dropdown now that today's Counts sheet exists.
        RefreshReferenceCountsDropdown(wb);

        // Update Worksheet
        UpdateWorksheetSheet(wb, fixtures, countsSheetName, existingRows, prevData);

        // Rebuild Rep Lists (deleted + recreated; pure derived output)
        BuildRepListsSheet(wb, fixtures, repDirectory);

        // Rebuild Quote and Phase sheets. They are pure ANCHORARRAY consumers
        // of Worksheet helper columns with no user-editable state. Copy+overwrite
        // workflows can duplicate spill formulas across re-saves, so rebuild fresh.
        RebuildQuoteAndPhaseSheets(wb);

        // Append changes
        if (prevData != null)
            AppendChanges(wb, fixtures, prevData);

        wb.Save();

        var spillSheets = new List<(string sheetName, int? minColumn)>
        {
            ("Worksheet", WsColActive),
            ("Quote", null),
            ("Phase 1", null),
            ("Phase 2", null),
            ("Phase 3", null),
        };
        PatchDynamicArrayMetadata(existingPath, spillSheets);
        EmbedHeaderFooterImages(existingPath, headerImagePath, footerImagePath,
            new[] { "Quote", "Phase 1", "Phase 2", "Phase 3" });
    }

    #region Cover Sheet

    private static void BuildCoverSheet(IXLWorkbook wb, string projectName, string projectLocation)
    {
        var ws = wb.Worksheets.Add("Cover");

        // Branding area (rows 1-4 left blank for user images)
        ws.Cell("A6").Value = "Project Name:";
        ws.Cell("B6").Value = projectName;
        ws.Cell("B6").Style.Font.Bold = true;
        ws.Cell("B6").Style.Font.FontSize = 14;

        ws.Cell("A7").Value = "Project Location:";
        ws.Cell("B7").Value = projectLocation ?? string.Empty;

        ws.Cell("A9").Value = "Lighting Fixture Quotation";
        ws.Cell("A9").Style.Font.Bold = true;
        ws.Cell("A9").Style.Font.FontSize = 16;

        ws.Cell("A11").Value = "Release Date:";
        ws.Cell("A12").Value = "Project Number:";
        ws.Cell("A13").Value = "For:";
        ws.Cell("A14").Value = "Prepared by:";
        ws.Cell("A15").Value = "Email:";
        ws.Cell("A16").Value = "Phone:";

        // Style labels
        for (int r = 6; r <= 16; r++)
        {
            if (r == 8 || r == 10) continue; // skip blank rows
            ws.Cell(r, 1).Style.Font.Bold = true;
        }

        // Column widths
        ws.Column(1).Width = 22;
        ws.Column(2).Width = 50;

        // Print area
        ApplyStandardPageSetup(ws);
        ws.PageSetup.PrintAreas.Add("A1:B17");
    }

    #endregion

    #region Dashboard Sheet

    // Dashboard cell anchors (consumed by helper pipeline via named ranges)
    private const string DashRepDirCell = "B3";
    private const string DashLutronCell = "B6";
    private const string DashFreightBuyCell = "B7";
    private const string DashFreightSellCell = "B8";
    private const string DashBidDateCell = "B11";
    private const string DashNotesFirstRow = "33";
    private const string DashNotesLastRow = "47";

    // Hidden helper range on Dashboard holding the parsed dates of every Counts sheet
    // currently in the workbook. Drives the Reference Counts dropdown's data-validation
    // list source. Refreshed on every build/update of the workbook.
    private const string DashCountsListFirstCell = "Z1";
    private const int DashCountsListMaxRows = 200;

    private static readonly XLColor HeaderBlue = XLColor.FromHtml("#4472C4");

    /// <summary>
    /// Creates the Dashboard sheet holding all workbook configuration, quote adjustments,
    /// the Reference Counts baseline pointer, internal notes, and the quote-footer notes
    /// library. All named ranges used by the helper pipeline and external readers are
    /// defined here.
    /// </summary>
    private static void BuildDashboardSheet(IXLWorkbook wb, string projectName, string repDirectoryPath)
    {
        var ws = wb.Worksheets.Add("Dashboard");
        ws.Position = 2; // after Cover

        // Sheet defaults — Segoe UI 11 matches the Counts/Changes raw tabs (and stays
        // dense enough that all 4 columns fit without scrolling).
        ws.Style.Font.FontName = "Segoe UI";
        ws.Style.Font.FontSize = 11;
        ws.Style.Alignment.WrapText = false;

        // Title row — same dark/amber strip the Worksheet uses for its header. Rows 1+2 share
        // the dark fill so we split the visual 45-height into 23 (title, top-aligned) + 22
        // (CONFIGURATION bar) — matches the 45 used elsewhere while letting row 2 host its own
        // section bar at the standard 22 height.
        ws.Cell("A1").Value = $" COUNTS DASHBOARD — {projectName}";
        StyleSectionBar(ws.Range("A1:D1"), fontSize: 16);
        ws.Range("A1:D1").Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        ws.Row(1).Height = 23;

        // --- CONFIGURATION ---
        WriteSectionBar(ws, 2, "CONFIGURATION");
        ws.Cell("A3").Value = "Rep Directory Path";
        ws.Range("B3:D3").Merge();
        ws.Cell("B3").Value = repDirectoryPath ?? string.Empty;
        // Border drawn around the full merged span — Excel won't paint inner cell edges
        // inside a merge, so a B3:C3 border would lose its right side. Top border is
        // omitted because the CONFIGURATION bar's #262626 bottom edge already closes the
        // top, and a gray rule there would bleed into the dark fill above.
        var b3d3 = ws.Range("B3:D3");
        var grayBorder = XLColor.FromHtml("#D9D9D9");
        b3d3.Style.Border.LeftBorder = XLBorderStyleValues.Thin;
        b3d3.Style.Border.LeftBorderColor = grayBorder;
        b3d3.Style.Border.RightBorder = XLBorderStyleValues.Thin;
        b3d3.Style.Border.RightBorderColor = grayBorder;
        b3d3.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        b3d3.Style.Border.BottomBorderColor = grayBorder;

        // --- QUOTE ADJUSTMENTS ---
        WriteSectionBar(ws, 5, "QUOTE ADJUSTMENTS");
        // Left blank by default — the Quote/Phase sheets omit the Lutron row when B6 is empty,
        // and blank cells coerce to 0 inside the Grand Total arithmetic.
        ws.Cell("A6").Value = "Lutron Lighting Control";
        ws.Cell("B6").Style.NumberFormat.Format = "$#,##0.00";
        StyleEditableCell(ws.Cell("B6"));
        // Sits directly under the QUOTE ADJUSTMENTS bar — drop the top border so the gray
        // outline doesn't show against the dark #262626 fill above.
        ws.Cell("B6").Style.Border.TopBorder = XLBorderStyleValues.None;

        ws.Cell("A7").Value = "Estimated Freight (Buy)";
        ws.Cell("B7").Style.NumberFormat.Format = "$#,##0.00";
        StyleEditableCell(ws.Cell("B7"));

        ws.Cell("A8").Value = "Estimated Freight (Sell)";
        ws.Cell("B8").Style.NumberFormat.Format = "$#,##0.00";
        StyleEditableCell(ws.Cell("B8"));

        // --- REFERENCE COUNTS ---
        // Live pointer to a historical Counts sheet. When set, Worksheet col E (and the
        // Quote Δ column it feeds) re-resolves against that snapshot via INDIRECT/SUMIFS.
        // When blank, behavior falls back to "compare against latest prior run."
        WriteSectionBar(ws, 10, "REFERENCE COUNTS");
        ws.Cell("A11").Value = "Compare to";
        ws.Cell("B11").Style.NumberFormat.Format = "yyyy-mm-dd";
        StyleEditableCell(ws.Cell("B11"));
        // Same dark-bar abutment fix as B6 — see comment above.
        ws.Cell("B11").Style.Border.TopBorder = XLBorderStyleValues.None;
        // Data-validation dropdown is wired up in RefreshReferenceCountsDropdown — that
        // helper also runs on every GenerateUpdate so the list stays in sync as new
        // Counts sheets accumulate.

        // Bold all column-A labels so they read as field captions against the input cells.
        foreach (string addr in new[] { "A3", "A6", "A7", "A8", "A11" })
            ws.Cell(addr).Style.Font.Bold = true;

        // --- INTERNAL NOTES ---
        WriteSectionBar(ws, 13, "INTERNAL NOTES");
        ws.Cell("A14").Value = "Date";
        ws.Cell("B14").Value = "Author";
        ws.Cell("C14").Value = "Status";
        ws.Cell("D14").Value = "Notes";
        StyleSubHeaderRow(ws.Range("A14:D14"));
        StyleInputBlock(ws.Range("A15:D29"), headerRow: ws.Range("A14:D14"));

        // --- QUOTE FOOTER NOTES ---
        WriteSectionBar(ws, 31, "QUOTE FOOTER NOTES");
        ws.Cell("A32").Value = "BOLD";
        ws.Cell("A32").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell("B32").Value = "Notes";
        StyleSubHeaderRow(ws.Range("A32:D32"));

        for (int i = 0; i < 15; i++)
        {
            int r = 33 + i;
            ws.Cell(r, 1).Value = false; // boolean literal — pass 3 upgrades to native checkbox
            ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            // Notes span B:D — merged so long notes aren't clipped by the narrow B column.
            ws.Range(r, 2, r, 4).Merge();
            ws.Cell(r, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        }
        StyleInputBlock(ws.Range("A33:D47"), headerRow: ws.Range("A32:D32"));

        // Column widths
        ws.Column(1).Width = 24;
        ws.Column(2).Width = 18;
        ws.Column(3).Width = 42;
        ws.Column(4).Width = 40;

        // Named ranges
        wb.DefinedNames.Add("RepDirectoryPath", ws.Range("B3:B3"));
        wb.DefinedNames.Add("LutronSubtotal", ws.Range("B6:B6"));
        wb.DefinedNames.Add("FreightBuy", ws.Range("B7:B7"));
        wb.DefinedNames.Add("FreightSell", ws.Range("B8:B8"));
        wb.DefinedNames.Add("BidDate", ws.Range("B11:B11"));
        wb.DefinedNames.Add("QuoteNotes", ws.Range($"B{DashNotesFirstRow}:B{DashNotesLastRow}"));
        wb.DefinedNames.Add("QuoteNotesBold", ws.Range($"A{DashNotesFirstRow}:A{DashNotesLastRow}"));

        // Protection: unlock editable cells, lock the rest
        foreach (string addr in new[] { "B3", "B6", "B7", "B8", "B11" })
            ws.Cell(addr).Style.Protection.SetLocked(false);
        ws.Range("A15:D29").Style.Protection.SetLocked(false);
        ws.Range($"A{DashNotesFirstRow}:B{DashNotesLastRow}").Style.Protection.SetLocked(false);
        ws.Protect().AllowElement(XLSheetProtectionElements.FormatColumns);

        ws.ShowGridLines = false;
        ApplyStandardPageSetup(ws);

        RefreshReferenceCountsDropdown(wb);
    }

    private static void EnsureDashboardSheet(IXLWorkbook wb, string repDirectoryPath)
    {
        if (wb.Worksheets.TryGetWorksheet("Dashboard", out _))
            return;

        IXLWorksheet? cover = null;
        if (wb.Worksheets.TryGetWorksheet("Cover", out var c))
        {
            cover = c;
            cover.Cell("A20").Clear();
            cover.Cell("B20").Clear();
            cover.Cell("A21").Clear();
            cover.Cell("B21").Clear();
            var old = wb.DefinedNames.FirstOrDefault(n =>
                string.Equals(n.Name, "PricingWorkbookPath", StringComparison.OrdinalIgnoreCase));
            old?.Delete();
        }

        string projectName = cover?.Cell("B6").GetString() ?? string.Empty;
        BuildDashboardSheet(wb, projectName, repDirectoryPath);
    }

    /// <summary>
    /// Repopulates the hidden helper range on Dashboard with the parsed dates of every
    /// Counts sheet currently in the workbook, then re-applies the data-validation list
    /// to B11. Called from BuildDashboardSheet and from GenerateUpdate so the dropdown
    /// stays in sync as new Counts sheets accumulate. Sheet protection is briefly
    /// suspended because the helper range and B11 are otherwise locked.
    /// </summary>
    /// <summary>
    /// Returns true if Dashboard!B11 holds a non-empty value (i.e. user has chosen a
    /// Reference Counts baseline). The actual sheet-name resolution happens at formula-
    /// evaluation time inside Excel via INDIRECT/TEXT — this only governs whether we
    /// emit the live formula or stay with the legacy literal write.
    /// </summary>
    private static bool HasReferenceCountsBaseline(IXLWorkbook wb)
    {
        if (!wb.Worksheets.TryGetWorksheet("Dashboard", out var ws))
            return false;
        var cell = ws.Cell("B11");
        if (cell.IsEmpty()) return false;
        // Numeric serial (date) or non-empty string both count.
        return !string.IsNullOrWhiteSpace(cell.GetFormattedString());
    }

    /// <summary>
    /// Builds the Worksheet col E formula that resolves to the Reference Counts SUMIFS
    /// when BidDate matches a historical Counts sheet, or the literal fallback (the value
    /// that would have been written under the legacy "vs. latest run" behavior) otherwise.
    /// </summary>
    private static string BuildReferenceCountsFormula(int row, double fallback)
    {
        // Preserve nothing-to-compare-to as blank so col F's IF(E="","",...) chain still
        // shows blank Δ for brand-new rows when fallback is 0.
        string fallbackLiteral = fallback == 0 ? "0" : fallback.ToString(System.Globalization.CultureInfo.InvariantCulture);
        const string sheetName = "\"'Counts \"&TEXT(BidDate,\"yyyy.mm.dd\")&\"'!\"";
        // Each INDIRECT pins one column on the resolved Counts sheet. SUMIFS sums col I
        // (Count) where col A (Type) matches A{row} and col S (CatCombo) matches A{row}|catalog.
        string sumIfs =
            $"SUMIFS(" +
            $"INDIRECT({sheetName}&\"I:I\")," +
            $"INDIRECT({sheetName}&\"A:A\"),A{row}," +
            $"INDIRECT({sheetName}&\"S:S\"),A{row}&\"|\"&C{row})";
        return $"IFERROR({sumIfs},{fallbackLiteral})";
    }

    private static void RefreshReferenceCountsDropdown(IXLWorkbook wb)
    {
        if (!wb.Worksheets.TryGetWorksheet("Dashboard", out var ws))
            return;

        bool wasProtected = ws.IsProtected;
        if (wasProtected)
            ws.Unprotect();

        // Clear prior helper range
        ws.Range($"Z1:Z{DashCountsListMaxRows}").Clear(XLClearOptions.Contents);

        var dates = new List<DateTime>();
        foreach (var cs in EnumerateCountsSheets(wb))
        {
            // Sheet name format: "Counts yyyy.MM.dd"
            string suffix = cs.Name.Length > 7 ? cs.Name.Substring(7) : string.Empty;
            if (DateTime.TryParseExact(suffix, "yyyy.MM.dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
            {
                dates.Add(dt);
            }
        }

        // Most recent first — helps the user pick the typical baseline (the latest priced bid).
        dates.Sort((a, b) => b.CompareTo(a));

        int count = Math.Min(dates.Count, DashCountsListMaxRows);
        for (int i = 0; i < count; i++)
        {
            var cell = ws.Cell(i + 1, 26); // col Z
            cell.Value = dates[i];
            cell.Style.NumberFormat.Format = "yyyy-mm-dd";
            cell.Style.Protection.SetLocked(true);
        }
        ws.Column(26).Hide();

        // Re-apply data validation to B11. Use a closed (non-empty) range so Excel doesn't
        // show every empty Z row as a blank entry.
        var b11 = ws.Cell("B11");
        b11.GetDataValidation().Clear();
        if (count > 0)
        {
            var listSource = $"=Dashboard!$Z$1:$Z${count}";
            var dv = b11.GetDataValidation();
            dv.List(listSource, true);
            dv.IgnoreBlanks = true;
        }
        b11.Style.Protection.SetLocked(false);

        if (wasProtected)
            ws.Protect().AllowElement(XLSheetProtectionElements.FormatColumns);
    }

    private static void WriteSectionBar(IXLWorksheet ws, int row, string text)
    {
        var rng = ws.Range(row, 1, row, 4);
        rng.Merge();
        // Leading spaces (rather than Alignment.Indent) so clicking Excel's Align Left
        // button can't collapse the buffer — the spaces are part of the value.
        rng.FirstCell().Value = " " + text;
        StyleSectionBar(rng, fontSize: 12);
        // Hard #262626 bottom edge so the side borders of any editable cell directly
        // below the bar (B6, B11, …) don't bleed up into the dark fill. Row 1 uses
        // StyleSectionBar directly and intentionally has no bottom rule (it abuts the
        // CONFIGURATION bar in row 2).
        rng.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        rng.Style.Border.BottomBorderColor = XLColor.FromHtml("#262626");
        ws.Row(row).Height = 22;
    }

    // Dashboard bars use the same dark-strip / amber-text scheme as the Worksheet
    // pricing-block header so the styling reads as one workbook. Slight left indent
    // so the label doesn't collide with the cell edge at small zoom levels.
    private static void StyleSectionBar(IXLRange rng, double fontSize)
    {
        rng.Style.Fill.BackgroundColor = XLColor.FromHtml("#262626");
        rng.Style.Font.FontColor = XLColor.FromHtml("#FACC75");
        rng.Style.Font.Bold = true;
        rng.Style.Font.FontSize = fontSize;
        rng.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        rng.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        // Buffer is provided via leading spaces in the value (see WriteSectionBar) — using
        // Alignment.Indent here would get cleared by Excel's Align Left toolbar button.
    }

    // Sub-header row inside a section (e.g. the column captions above Internal Notes
    // and Quote Footer Notes). Light fill + thin dark bottom border — quieter than a
    // section bar but still clearly demarcates the data block underneath.
    private static void StyleSubHeaderRow(IXLRange rng)
    {
        rng.Style.Font.Bold = true;
        rng.Style.Fill.BackgroundColor = XLColor.FromHtml("#F2F2F2");
        rng.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        // Same #A6A6A6 as the data-block outline below — without this match the underline
        // reads as a hard dark rule that disagrees with the rest of the section frame.
        rng.Style.Border.BottomBorderColor = XLColor.FromHtml("#A6A6A6");
    }

    // Editable single cell — thin gray box border so the input target is visible
    // without gridlines.
    private static void StyleEditableCell(IXLCell cell)
    {
        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#D9D9D9");
    }

    // Multi-row input block (Internal Notes table, Quote Footer Notes table).
    // Thin gray grid inside the data rows, slightly darker outside around the whole
    // block (header row included when supplied) — same visual language as the
    // Counts/Changes raw tabs.
    private static void StyleInputBlock(IXLRange rng, IXLRange? headerRow = null)
    {
        rng.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        rng.Style.Border.InsideBorderColor = XLColor.FromHtml("#D9D9D9");

        var outline = headerRow == null
            ? rng
            : rng.Worksheet.Range(
                headerRow.FirstRow().RowNumber(),
                Math.Min(headerRow.FirstColumn().ColumnNumber(), rng.FirstColumn().ColumnNumber()),
                rng.LastRow().RowNumber(),
                Math.Max(headerRow.LastColumn().ColumnNumber(), rng.LastColumn().ColumnNumber()));
        outline.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        outline.Style.Border.OutsideBorderColor = XLColor.FromHtml("#A6A6A6");
    }

    #endregion

    #region Rep Directory + Rep Lists

    internal class RepEntry
    {
        public string Rep { get; init; } = string.Empty;
        public string QuoteContact { get; init; } = string.Empty;
        public string OrderContact { get; init; } = string.Empty;
        public XLColor? HeaderFill { get; init; }
    }

    /// <summary>
    /// Reads the external Rep Directory workbook and returns a case-insensitive
    /// Mfr→RepEntry map. Rep/Quote/Order values are each merged blocks in the source
    /// (cols A/H/I); every Mfr row in column B inherits its block's rep metadata.
    /// Silently returns an empty dictionary on any failure.
    /// </summary>
    private static Dictionary<string, RepEntry> ReadRepDirectory(string? path)
    {
        var empty = new Dictionary<string, RepEntry>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return empty;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var wb = new XLWorkbook(fs);
            var ws = wb.Worksheets.First();
            int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

            // Pre-index merged ranges by column for O(log n) containment lookup
            var mergedByCol = new Dictionary<int, List<IXLRange>>();
            foreach (var mr in ws.MergedRanges)
            {
                int c = mr.FirstColumn().ColumnNumber();
                if (!mergedByCol.TryGetValue(c, out var list))
                    mergedByCol[c] = list = new List<IXLRange>();
                list.Add(mr);
            }

            static string ExtractText(IXLCell cell)
            {
                // Handles plain text, HYPERLINK() formulas (2nd arg is display text),
                // cached formula results, and cells whose only content is an attached hyperlink.
                try
                {
                    string formatted = cell.GetFormattedString();
                    if (!string.IsNullOrWhiteSpace(formatted) && !formatted.StartsWith("="))
                        return formatted;
                }
                catch { }
                try
                {
                    var v = cell.CachedValue;
                    string s = v.ToString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                catch { }
                if (cell.HasFormula)
                {
                    string f = cell.FormulaA1 ?? string.Empty;
                    // Pull display text out of =HYPERLINK("url","text")
                    int comma = f.IndexOf(',');
                    if (f.StartsWith("HYPERLINK(", StringComparison.OrdinalIgnoreCase) && comma > 0)
                    {
                        string tail = f.Substring(comma + 1).TrimEnd(')').Trim().Trim('"');
                        if (!string.IsNullOrWhiteSpace(tail)) return tail;
                    }
                }
                if (cell.HasHyperlink)
                {
                    string tip = cell.GetHyperlink().Tooltip ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(tip)) return tip;
                }
                return cell.GetString();
            }

            string ResolveBlockValue(int col, int row)
            {
                string direct = ExtractText(ws.Cell(row, col));
                if (!string.IsNullOrWhiteSpace(direct)) return direct;
                if (mergedByCol.TryGetValue(col, out var list))
                {
                    foreach (var mr in list)
                    {
                        if (row >= mr.FirstRow().RowNumber() && row <= mr.LastRow().RowNumber())
                            return ExtractText(mr.FirstCell());
                    }
                }
                return string.Empty;
            }

            static XLColor? SampleFill(IXLCell cell)
            {
                try
                {
                    var fill = cell.Style.Fill;
                    if (fill.PatternType == XLFillPatternValues.None) return null;
                    var bg = fill.BackgroundColor;
                    var sd = bg.Color; // System.Drawing.Color
                    if (sd.A == 0) return null;
                    if (sd.R == 255 && sd.G == 255 && sd.B == 255) return null;
                    return bg;
                }
                catch { return null; }
            }

            XLColor? ResolveBlockFill(int col, int row)
            {
                var direct = SampleFill(ws.Cell(row, col));
                if (direct != null) return direct;
                if (mergedByCol.TryGetValue(col, out var list))
                {
                    foreach (var mr in list)
                    {
                        if (row >= mr.FirstRow().RowNumber() && row <= mr.LastRow().RowNumber())
                        {
                            var anchor = SampleFill(mr.FirstCell());
                            if (anchor != null) return anchor;
                        }
                    }
                }
                return null;
            }

            var result = new Dictionary<string, RepEntry>(StringComparer.OrdinalIgnoreCase);
            for (int r = 2; r <= lastRow; r++)
            {
                string mfr = ExtractText(ws.Cell(r, 2)).Trim();
                if (string.IsNullOrWhiteSpace(mfr)) continue;

                var entry = new RepEntry
                {
                    Rep = ResolveBlockValue(1, r),
                    QuoteContact = ResolveBlockValue(8, r),
                    OrderContact = ResolveBlockValue(9, r),
                    HeaderFill = ResolveBlockFill(1, r),
                };

                // Expand aliases ("Element/Tech", "Foo A.K.A. Bar") and index each under its normalized key.
                foreach (string alias in ExpandMfrAliases(mfr))
                {
                    string key = NormalizeMfr(alias);
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    result.TryAdd(key, entry);
                }
            }
            return result;
        }
        catch
        {
            return empty;
        }
    }

    private static string[] SplitLines(string s) =>
        string.IsNullOrEmpty(s)
            ? Array.Empty<string>()
            : s.Replace("\r\n", "\n").Replace('\r', '\n')
               .Split('\n', StringSplitOptions.None)
               .Select(p => p.Trim())
               .Where(p => p.Length > 0)
               .ToArray();

    private static string JoinLines(string s, string sep) => string.Join(sep, SplitLines(s));

    private static readonly string[] MfrSuffixNoise =
    {
        "lighting", "lights", "light", "ltg",
        "inc", "llc", "corp", "corporation", "co", "company",
        "industries", "ltd", "group", "usa",
    };

    // Display-trim noise set (case-insensitive). Decoupled from MfrSuffixNoise so display
    // tweaks don't perturb Rep Lists matching. Intentionally omits "lights" (kept for matching).
    private static readonly HashSet<string> MfrDisplayNoise =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "lighting", "light", "ltg",
            "inc", "llc", "corp", "corporation", "co", "company",
            "industries", "ltd", "group", "usa",
        };

    /// <summary>
    /// Space-saving display trim for the Worksheet Mfr column. Strips trailing legal
    /// suffixes (Inc, LLC, Corp, Co, Company, Ltd, Industries, Group, USA) and lighting
    /// tokens (Lighting, Lights, Light, Ltg), then drops a trailing "and"/"&" left dangling
    /// by the strip (e.g. "AV Poles and Lighting" → "AV Poles"). Stops at the first
    /// non-noise token, never reduces below one remaining word, and preserves original
    /// casing. Unlike NormalizeMfr this is cosmetic — not used for matching.
    /// </summary>
    private static string TrimMfrForDisplay(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw ?? string.Empty;

        var tokens = raw.Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        static string StripPunct(string t) => t.Trim(',', '.', ';', ':');

        bool strippedAny = false;
        while (tokens.Count > 1 && MfrDisplayNoise.Contains(StripPunct(tokens[^1])))
        {
            tokens.RemoveAt(tokens.Count - 1);
            strippedAny = true;
        }
        if (strippedAny && tokens.Count > 1)
        {
            string last = StripPunct(tokens[^1]);
            if (last.Equals("and", StringComparison.OrdinalIgnoreCase) || last == "&")
                tokens.RemoveAt(tokens.Count - 1);
        }
        if (tokens.Count > 0)
            tokens[^1] = tokens[^1].TrimEnd(',', ';');

        return string.Join(" ", tokens);
    }

    private static readonly string[] MfrAliasDelimiters =
    {
        " a.k.a. ", " aka ", "/", "\r\n", "\n",
    };

    /// <summary>
    /// Splits a directory Mfr cell into its alternate names. "Element/Tech" → ["Element","Tech"].
    /// Newline-separated entries (a directory cell holding multiple mfrs on separate lines) and
    /// "Foo A.K.A. Bar" aliases are also split. Always yields at least the original trimmed string.
    /// </summary>
    private static IEnumerable<string> ExpandMfrAliases(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        var pieces = new List<string> { raw };
        foreach (string delim in MfrAliasDelimiters)
        {
            var next = new List<string>();
            foreach (string p in pieces)
                next.AddRange(p.Split(new[] { delim }, StringSplitOptions.RemoveEmptyEntries));
            pieces = next;
        }
        foreach (string p in pieces)
        {
            string t = p.Trim();
            if (!string.IsNullOrWhiteSpace(t)) yield return t;
        }
    }

    /// <summary>
    /// Normalizes a Mfr name for equality matching: lowercases, strips punctuation, collapses
    /// whitespace, and removes trailing generic-suffix tokens (Lighting, Inc, LLC, etc.) so that
    /// "Pure Edge Lighting" and "Pure Edge" collapse to the same key.
    /// </summary>
    private static string NormalizeMfr(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char ch in raw)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '.' || ch == ',' || ch == '&' || ch == '\'')
                sb.Append(' ');
            // other punctuation dropped
        }

        var tokens = sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        // Strip trailing generic-suffix tokens, but never down to zero tokens.
        while (tokens.Count > 1 && MfrSuffixNoise.Contains(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);

        return string.Join(" ", tokens);
    }

    /// <summary>
    /// Builds the Rep Lists sheet between Worksheet and Quote. One block per matched rep,
    /// an Unmatched block at the end for mfrs not in the directory. Rebuilt fresh each
    /// time (delete + add) since it has no user-editable state.
    /// </summary>
    private static void BuildRepListsSheet(
        IXLWorkbook wb,
        List<CountsFixtureModel> fixtures,
        Dictionary<string, RepEntry> directory)
    {
        // Delete if exists (GenerateUpdate path)
        if (wb.Worksheets.TryGetWorksheet("Rep Lists", out var existing))
            existing.Delete();

        var ws = wb.Worksheets.Add("Rep Lists");
        // Position between Worksheet and Quote
        if (wb.Worksheets.TryGetWorksheet("Worksheet", out var wsSheet))
            ws.Position = wsSheet.Position + 1;

        // Read project info from Cover sheet so each block carries the job identifiers
        // (pricing team copies a whole block into an email — name/location need to travel with it).
        string projectName = string.Empty;
        string projectLocation = string.Empty;
        if (wb.Worksheets.TryGetWorksheet("Cover", out var coverWs))
        {
            projectName = coverWs.Cell("B6").GetString().Trim();
            projectLocation = coverWs.Cell("B7").GetString().Trim();
        }

        // Mfr Override snapshot from Worksheet!U. Overrides are emergency substitutions for
        // the Revit-authored Mfr — they apply to both the displayed literal and the rep-group
        // lookup. User must type the canonical Mfr name (matching the directory key), not a
        // display alias; mistyped overrides fall into Unmatched.
        var mfrOverrideByKey = new Dictionary<(string Type, string Catalog), string>();
        if (wb.Worksheets.TryGetWorksheet("Worksheet", out var wsSrc))
        {
            int lr = wsSrc.LastRowUsed()?.RowNumber() ?? 1;
            for (int r = 2; r <= lr; r++)
            {
                string t = wsSrc.Cell(r, WsColType).GetString();
                string c = wsSrc.Cell(r, WsColCatalog).GetString();
                string ovr = wsSrc.Cell(r, WsColMfrOverride).GetString();
                if (string.IsNullOrWhiteSpace(t) || string.IsNullOrWhiteSpace(c)) continue;
                if (!string.IsNullOrWhiteSpace(ovr))
                    mfrOverrideByKey[(t.ToUpperInvariant(), c.ToUpperInvariant())] = ovr;
            }
        }

        // Flatten fixtures to (Type, Mfr, Catalog, Qty) rows, substituting effective Mfr.
        var rows = new List<(string Type, string Mfr, string Catalog, int Qty)>();
        foreach (var f in fixtures)
        {
            for (int c = 0; c < 6; c++)
            {
                string cat = f.CatalogNumbers[c] ?? "";
                if (string.IsNullOrWhiteSpace(cat)) continue;
                string effMfr = mfrOverrideByKey.TryGetValue(
                    (f.TypeMark.ToUpperInvariant(), cat.ToUpperInvariant()), out var ovr)
                        ? ovr
                        : f.Manufacturer;
                rows.Add((f.TypeMark, effMfr, cat, f.Count));
            }
        }

        // Group by rep (matched) vs unmatched
        var matched = new Dictionary<string, (RepEntry Entry, List<(string Type, string Mfr, string Catalog, int Qty)> Items)>(
            StringComparer.OrdinalIgnoreCase);
        var unmatched = new List<(string Type, string Mfr, string Catalog, int Qty)>();

        foreach (var row in rows)
        {
            string key = NormalizeMfr(row.Mfr);
            if (!string.IsNullOrWhiteSpace(key) &&
                directory.TryGetValue(key, out var entry) &&
                !string.IsNullOrWhiteSpace(entry.Rep))
            {
                if (!matched.TryGetValue(entry.Rep, out var bucket))
                    matched[entry.Rep] = bucket = (entry, new List<(string, string, string, int)>());
                bucket.Items.Add(row);
            }
            else
            {
                unmatched.Add(row);
            }
        }

        // Catalogs that appear under more than one rep block — flagged per-cell so pricing
        // notices when an Mfr Override has re-routed a fixture to a different rep but the
        // Rep List still shows the pre-override row (Rep Lists only re-bucket on Revit update).
        var repsByCatalog = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void TrackCatalogRep(string catalog, string repName)
        {
            if (!repsByCatalog.TryGetValue(catalog, out var set))
                repsByCatalog[catalog] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(repName);
        }
        foreach (var (repName, bucket) in matched)
            foreach (var it in bucket.Items) TrackCatalogRep(it.Catalog, repName);
        foreach (var it in unmatched) TrackCatalogRep(it.Catalog, "__unmatched__");
        var crossRepCatalogs = new HashSet<string>(
            repsByCatalog.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key),
            StringComparer.OrdinalIgnoreCase);

        // Track max data-cell length per column (1–4), widest rep-bar line, and widest contact lines per half.
        var colMax = new[] { "Type".Length, "Mfr".Length, "Catalog Number".Length, "Qty".Length };
        int repBarMax = 0;
        int quoteMax = 0;
        int orderMax = 0;

        int curRow = 1;
        foreach (var repName in matched.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var (entry, items) = matched[repName];
            curRow = WriteRepBlock(ws, curRow, entry.Rep, entry.QuoteContact, entry.OrderContact,
                projectName, projectLocation, items,
                entry.HeaderFill ?? HeaderBlue,
                colMax, ref repBarMax, ref quoteMax, ref orderMax,
                crossRepCatalogs);
            curRow++; // spacer
            ws.PageSetup.AddHorizontalPageBreak(curRow);
        }

        if (unmatched.Count > 0 || matched.Count == 0)
        {
            curRow = WriteRepBlock(ws, curRow, "UNMATCHED MANUFACTURERS", string.Empty, string.Empty,
                projectName, projectLocation, unmatched,
                XLColor.FromHtml("#F1A983"), colMax, ref repBarMax, ref quoteMax, ref orderMax,
                crossRepCatalogs);
        }

        // Column widths: max content length + padding, with sensible minimums.
        double[] widths =
        {
            Math.Max(8,  colMax[0] + 2),
            Math.Max(14, colMax[1] + 2),
            Math.Max(18, colMax[2] + 2),
            Math.Max(6,  colMax[3] + 2),
        };
        // A:B merged holds quote-contact lines → ensure widths[0]+widths[1] fits the widest quote line.
        double leftHalf = widths[0] + widths[1];
        if (quoteMax + 2 > leftHalf) widths[1] += (quoteMax + 2 - leftHalf);
        // C:D merged holds order-contact lines → ensure widths[2]+widths[3] fits the widest order line.
        double rightHalf = widths[2] + widths[3];
        if (orderMax + 2 > rightHalf) widths[3] += (orderMax + 2 - rightHalf);
        // Rep bar spans A:D → ensure full row can display the flattened rep name.
        double total = widths.Sum();
        if (repBarMax + 2 > total) widths[2] += (repBarMax + 2 - total);

        for (int i = 0; i < 4; i++) ws.Column(i + 1).Width = widths[i];

        // Left-align Qty so it stays visually adjacent to Catalog when the column is wide.
        ws.Column(4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        ApplyStandardPageSetup(ws);
        ws.ShowGridLines = false;
    }

    // Project name/location rows are rendered taller than default, with vertical alignment
    // pushing the text away from the adjacent sections — the empty portion of the cell
    // supplies the visual buffer without needing separate spacer rows.
    private const double RepBlockProjectRowHeight = 22.0;

    private static int WriteRepBlock(
        IXLWorksheet ws, int startRow,
        string repName, string quoteContact, string orderContact,
        string projectName, string projectLocation,
        List<(string Type, string Mfr, string Catalog, int Qty)> items,
        XLColor headerColor,
        int[] colMax, ref int repBarMax, ref int quoteMax, ref int orderMax,
        HashSet<string> crossRepCatalogs)
    {
        int row = startRow;

        // Rep name bar — flatten any newlines in the directory cell to " / " for the single-line header.
        string repTitle = JoinLines(repName, " / ");
        var hdr = ws.Range(row, 1, row, 4);
        hdr.Merge();
        hdr.FirstCell().Value = repTitle;
        hdr.Style.Fill.BackgroundColor = headerColor;
        // Pick black or white text based on fill luminance (YIQ).
        var sd = headerColor.Color;
        double luminance = 0.299 * sd.R + 0.587 * sd.G + 0.114 * sd.B;
        hdr.Style.Font.FontColor = luminance > 160 ? XLColor.Black : XLColor.White;
        hdr.Style.Font.Bold = true;
        hdr.Style.Font.FontSize = 12;
        if (repTitle.Length > repBarMax) repBarMax = repTitle.Length;
        row++;

        // Contact section — side-by-side line-by-line. Left half (A:B) = quote lines; right half (C:D) = order lines.
        var quoteLines = SplitLines(quoteContact);
        var orderLines = SplitLines(orderContact);
        int contactRowCount = Math.Max(quoteLines.Length, orderLines.Length);
        // Prepend a "label" row when either side has content.
        if (contactRowCount > 0)
        {
            ws.Range(row, 1, row, 2).Merge().FirstCell().Value = "Quote Contact";
            ws.Range(row, 3, row, 4).Merge().FirstCell().Value = "Order Contact";
            var labelRow = ws.Range(row, 1, row, 4);
            labelRow.Style.Font.Bold = true;
            labelRow.Style.Font.FontColor = XLColor.DimGray;
            labelRow.Style.Border.BottomBorder = XLBorderStyleValues.Hair;
            if ("Quote Contact".Length > quoteMax) quoteMax = "Quote Contact".Length;
            if ("Order Contact".Length > orderMax) orderMax = "Order Contact".Length;
            row++;

            var emailColor = XLColor.FromHtml("#9A9A9A"); // light gray, paired with italic
            for (int i = 0; i < contactRowCount; i++)
            {
                string q = i < quoteLines.Length ? quoteLines[i] : string.Empty;
                string o = i < orderLines.Length ? orderLines[i] : string.Empty;
                var lq = ws.Range(row, 1, row, 2).Merge();
                var lo = ws.Range(row, 3, row, 4).Merge();
                lq.FirstCell().Value = q;
                lo.FirstCell().Value = o;
                if (q.Contains('@'))
                {
                    lq.Style.Font.FontColor = emailColor;
                    lq.Style.Font.Italic = true;
                }
                else
                {
                    lq.Style.Font.FontColor = XLColor.DimGray;
                }
                if (o.Contains('@'))
                {
                    lo.Style.Font.FontColor = emailColor;
                    lo.Style.Font.Italic = true;
                }
                else
                {
                    lo.Style.Font.FontColor = XLColor.DimGray;
                }
                if (q.Length > quoteMax) quoteMax = q.Length;
                if (o.Length > orderMax) orderMax = o.Length;
                row++;
            }
        }

        // Project identifiers — each block carries its own name+location so a pricing-team
        // paste of the whole block into an email always includes the job reference.
        // Rows are rendered tall with vertical alignment pushing text outward, so the
        // empty cell padding supplies the top/bottom buffer around this section.
        bool hasProjectName = !string.IsNullOrWhiteSpace(projectName);
        bool hasProjectLocation = !string.IsNullOrWhiteSpace(projectLocation);
        bool hasBoth = hasProjectName && hasProjectLocation;

        if (hasProjectName)
        {
            var pn = ws.Range(row, 1, row, 4).Merge();
            pn.FirstCell().Value = projectName;
            pn.Style.Font.Bold = true;
            pn.Style.Alignment.Vertical = hasBoth
                ? XLAlignmentVerticalValues.Bottom   // push away from contacts above
                : XLAlignmentVerticalValues.Center;  // only row → center for symmetric buffer
            ws.Row(row).Height = RepBlockProjectRowHeight;
            if (projectName.Length > colMax[2]) colMax[2] = projectName.Length;
            row++;
        }
        if (hasProjectLocation)
        {
            var pl = ws.Range(row, 1, row, 4).Merge();
            pl.FirstCell().Value = projectLocation;
            pl.Style.Font.Bold = true;
            pl.Style.Alignment.Vertical = hasBoth
                ? XLAlignmentVerticalValues.Top      // push away from column headers below
                : XLAlignmentVerticalValues.Center;
            ws.Row(row).Height = RepBlockProjectRowHeight;
            if (projectLocation.Length > colMax[2]) colMax[2] = projectLocation.Length;
            row++;
        }

        // Column headers
        ws.Cell(row, 1).Value = "Type";
        ws.Cell(row, 2).Value = "Mfr";
        ws.Cell(row, 3).Value = "Catalog Number";
        ws.Cell(row, 4).Value = "Qty";
        var colHdr = ws.Range(row, 1, row, 4);
        colHdr.Style.Font.Bold = true;
        colHdr.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        row++;

        // Data rows — Qty pulls live from Worksheet col D (Calc-adjusted) keyed by (Type, Catalog)
        // so pricing-team Calc changes propagate here without re-running Counts.
        foreach (var item in items)
        {
            ws.Cell(row, 1).Value = item.Type;
            ws.Cell(row, 2).Value = item.Mfr;
            ws.Cell(row, 3).Value = item.Catalog;
            ws.Cell(row, 4).FormulaA1 =
                $"SUMIFS(Worksheet!BL:BL,Worksheet!A:A,A{row},Worksheet!C:C,C{row})";

            // Flag catalogs that exist under multiple rep blocks — typically the fallout of
            // an Mfr Override that moved a fixture's rep but left an older row behind until
            // the next Revit update regenerates the Rep List.
            if (crossRepCatalogs.Contains(item.Catalog))
            {
                ws.Cell(row, 3).Style.Fill.BackgroundColor = YellowFill;
                ws.Cell(row, 3).GetComment().AddText(
                    "Catalog number appears under another rep block. Set Mfr. Override and re-run from Revit to rebuild rep blocks.");
            }

            if (item.Type.Length    > colMax[0]) colMax[0] = item.Type.Length;
            if (item.Mfr.Length     > colMax[1]) colMax[1] = item.Mfr.Length;
            if (item.Catalog.Length > colMax[2]) colMax[2] = item.Catalog.Length;
            int qtyLen = item.Qty.ToString().Length;
            if (qtyLen              > colMax[3]) colMax[3] = qtyLen;

            row++;
        }

        return row;
    }

    #endregion

    #region Counts Sheet

    private static void BuildCountsSheet(IXLWorkbook wb, List<CountsFixtureModel> fixtures, string sheetName)
    {
        var ws = wb.Worksheets.Add(sheetName);

        // Headers
        ws.Cell(1, CsColType).Value = "Type";
        ws.Cell(1, CsColMfr).Value = "Mfr";
        ws.Cell(1, CsColCat1).Value = "Cat 1";
        ws.Cell(1, CsColCat2).Value = "Cat 2";
        ws.Cell(1, CsColCat3).Value = "Cat 3";
        ws.Cell(1, CsColCat4).Value = "Cat 4";
        ws.Cell(1, CsColCat5).Value = "Cat 5";
        ws.Cell(1, CsColCat6).Value = "Cat 6";
        ws.Cell(1, CsColCount).Value = "Count";
        ws.Cell(1, CsColLinear).Value = "Linear Length";
        ws.Cell(1, CsColReel).Value = "Reel Length";
        ws.Cell(1, CsColChannel).Value = "Channel Length";
        for (int n = 0; n < 6; n++)
            ws.Cell(1, CsColNote1 + n).Value = $"Schedule Notes {n + 1}";
        ws.Cell(1, CsColCatCombo).Value = "_CatCombo";

        // Data rows
        int row = 2;
        foreach (var f in fixtures)
        {
            ws.Cell(row, CsColType).Value = f.TypeMark;
            ws.Cell(row, CsColMfr).Value = f.Manufacturer;
            for (int c = 0; c < 6; c++)
                ws.Cell(row, CsColCat1 + c).Value = f.CatalogNumbers[c] ?? "";
            ws.Cell(row, CsColCount).Value = f.Count;
            ws.Cell(row, CsColLinear).Value = Math.Round(f.LinearLength, 2);
            ws.Cell(row, CsColReel).Value = Math.Round(f.ReelLength, 2);
            ws.Cell(row, CsColChannel).Value = Math.Round(f.ChannelLength, 2);
            for (int n = 0; n < 6; n++)
                ws.Cell(row, CsColNote1 + n).Value = f.Notes[n] ?? string.Empty;
            ws.Cell(row, CsColCatCombo).FormulaA1 = BuildCatComboFormula(row);
            row++;
        }
        int lastDataRow = row - 1;
        ws.Column(CsColCatCombo).Hide();

        ApplyRawSheetStyling(ws, CsColNote6, lastDataRow);

        // Auto-fit columns against the typography set above (Segoe UI 11).
        ws.Columns().AdjustToContents();
        // Cap Schedule Notes columns — overflow is fine, long notes shouldn't balloon the raw sheet.
        for (int n = 0; n < 6; n++)
        {
            if (ws.Column(CsColNote1 + n).Width > 22)
                ws.Column(CsColNote1 + n).Width = 22;
        }
        // Cap catalog columns too — keeps all 18 columns on screen for typical catalog lengths.
        for (int c = 0; c < 6; c++)
        {
            if (ws.Column(CsColCat1 + c).Width > 18)
                ws.Column(CsColCat1 + c).Width = 18;
        }
    }

    #endregion

    #region Worksheet Sheet

    private static void BuildWorksheetSheet(
        IXLWorkbook wb,
        List<CountsFixtureModel> fixtures,
        string countsSheetName,
        Dictionary<(string Type, string Catalog), WorksheetRowData>? existingRows)
    {
        var ws = wb.Worksheets.Add("Worksheet");

        // Headers
        ws.Cell(1, WsColType).Value = "Type";
        ws.Cell(1, WsColMfr).Value = "Mfr";
        ws.Cell(1, WsColCatalog).Value = "Catalog Number";
        ws.Cell(1, WsColQty).Value = "Qty";
        ws.Cell(1, WsColPrevQty).Value = "Prev";
        ws.Cell(1, WsColDelta).Value = "Δ";
        ws.Cell(1, WsColCalc).Value = "Calc";
        ws.Cell(1, WsColPhase).Value = "Phase";
        ws.Cell(1, WsColDesc).Value = "Description";
        ws.Cell(1, WsColUnitCost).Value = "Unit Cost";
        ws.Cell(1, WsColMarkup).Value = "Markup";
        ws.Cell(1, WsColTariff).Value = "Tariff";
        ws.Cell(1, WsColAdder).Value = "Adder";
        for (int n = 0; n < 6; n++)
            ws.Cell(1, WsColNote1 + n).Value = $"Note {n + 1}";
        ws.Cell(1, WsColMfrOverride).Value = "Mfr Override";
        ws.Cell(1, WsColQtyOverride).Value = "Qty Override";
        ws.Cell(1, WsColEffQty).Value = "EffQty";

        // Style headers — uniform #262626 background and bold; per-section font colors below.
        ApplyWorksheetHeaderStyling(ws);

        string csRef = $"'{countsSheetName}'";

        // Track first occurrence of each catalog number (canonical source for per-catalog
        // pricing fields: Description, Calc, Unit Cost) and each Type (source for per-Type Tariff).
        var catalogFirstRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var typeFirstRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Pre-pass: count catalog occurrences — used to mark the canonical row bold only when
        // the catalog appears on multiple rows (single-row catalogs aren't visually distinguished).
        var catalogCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fixtures)
        {
            for (int c = 0; c < 6; c++)
            {
                string cn = f.CatalogNumbers[c] ?? "";
                if (string.IsNullOrWhiteSpace(cn)) continue;
                catalogCounts[cn] = catalogCounts.GetValueOrDefault(cn) + 1;
            }
        }

        int row = 2;
        foreach (var f in fixtures)
        {
            for (int c = 0; c < 6; c++)
            {
                string catNum = f.CatalogNumbers[c] ?? "";
                if (string.IsNullOrWhiteSpace(catNum)) continue;

                // Type, Mfr, Catalog Number
                ws.Cell(row, WsColType).Value = f.TypeMark;
                ws.Cell(row, WsColMfr).Value = TrimMfrForDisplay(f.Manufacturer);
                ws.Cell(row, WsColCatalog).Value = catNum;
                ws.Cell(row, WsColTypeRepeat).Value = f.TypeMark;

                // Qty formula
                string qtyFormula = BuildQtyFormula(row, csRef);
                ws.Cell(row, WsColQty).FormulaA1 = qtyFormula;

                // Prev Qty — blank on first export
                // Delta formula
                ws.Cell(row, WsColDelta).FormulaA1 = $"IF(E{row}=\"\",\"\",D{row}-E{row})";

                // Calc dropdown
                ws.Cell(row, WsColCalc).GetDataValidation().List("\"Reel,Channel,End Cap,Clip\"", true);

                // Catalog-canonical fields (Description, Calc, Unit Cost): subsequent occurrences
                // reference the first. Markup % and Adder have NO canonical — users drag-fill.
                if (catalogFirstRow.TryGetValue(catNum, out int firstRow))
                {
                    // Desc / Unit Cost: empty canonical → "dependent" placeholder so the link
                    // is visible. Calc: plain ref (no wrap) — dropdown cell stays usable.
                    ws.Cell(row, WsColDesc).FormulaA1 = DependentFormula("H", firstRow);
                    ws.Cell(row, WsColCalc).FormulaA1 = $"IF(I{firstRow}=\"\",\"\",I{firstRow})";
                    ws.Cell(row, WsColUnitCost).FormulaA1 = DependentFormula("J", firstRow);
                    StyleAutoFilledCell(ws.Cell(row, WsColDesc));
                    StyleAutoFilledCell(ws.Cell(row, WsColCalc));
                    StyleAutoFilledCell(ws.Cell(row, WsColUnitCost));
                }
                else
                {
                    catalogFirstRow[catNum] = row;
                    // Description + Unit Cost start blank — pricing team enters manually.

                    // Mark canonical only when the catalog has siblings (otherwise every row would be bolded)
                    if (catalogCounts.GetValueOrDefault(catNum) > 1)
                        MarkCanonicalRow(ws, row, catNum);
                }

                // Tariff % (K) is per-Type canonical: only the first row of each Type holds a
                // literal; subsequent rows mirror it via a gray-italic formula (WriteTariffCell)
                // so users see the tariff on every row while the canonical cell remains the
                // only editable one. Helper pipeline resolves the Type's tariff % via XLOOKUP
                // against the full K column (first match = canonical).
                //
                // Schedule Notes (N–S) follow the same per-Type pattern: canonical row seeds
                // from Revit on first creation; subsequent rows show "---" in gray italic.
                if (!typeFirstRow.ContainsKey(f.TypeMark))
                {
                    typeFirstRow[f.TypeMark] = row;
                    for (int n = 0; n < 6; n++)
                        WriteNoteCell(ws, row, row, n, f.Notes[n]);
                }
                else
                {
                    WriteTariffCell(ws, row, typeFirstRow[f.TypeMark], existingTariff: null, isNewRow: true);
                    for (int n = 0; n < 6; n++)
                        WriteNoteCell(ws, row, typeFirstRow[f.TypeMark], n, null);
                }

                // EffQty (BQ) = QtyOverride (U) if present, else Revit Qty (D). Single source
                // of truth for effective qty — Rep Lists SUMIFS and the helper pipeline read
                // this column instead of duplicating the override fallback.
                ws.Cell(row, WsColEffQty).FormulaA1 = $"IF(V{row}=\"\",D{row},V{row})";
                ws.Cell(row, WsColEffQty).Style.NumberFormat.Format = "0";

                // Active flag: initial export has no removed rows — always 1.
                ws.Cell(row, WsColActive).Value = 1;

                row++;
            }
        }

        int lastDataRow = row - 1;

        // Apply typography first so AdjustToContents below measures against Segoe UI 12
        // (the sheet font) rather than ClosedXML's default. Col G's auto-fit lives inside
        // ApplyWorksheetTypography because it uses size 11.
        ApplyWorksheetTypography(ws);

        // Column widths
        ws.Column(WsColType).AdjustToContents();
        ws.Column(WsColMfr).AdjustToContents();
        ws.Column(WsColCatalog).AdjustToContents();
        ApplyQtyColumnFormatting(ws);
        ws.Column(WsColDesc).Width = 25;
        ws.Column(WsColCalc).Width = 10;
        ws.Column(WsColUnitCost).Width = 12;
        ws.Column(WsColMarkup).Width = 10;
        ws.Column(WsColTariff).Width = 10;
        ws.Column(WsColAdder).Width = 10;
        ws.Column(WsColPhase).Width = 10;
        // Note columns N–S: 16.64 width, no wrap — long notes overflow into adjacent empty
        // cells. Prioritizes compactness over full visibility for rarely-edited content.
        for (int n = 0; n < 6; n++)
            ws.Column(WsColNote1 + n).Width = 16.64;
        // 16.64 char-width ≈ 190px in the target environment. Matched widths on the two override columns.
        ws.Column(WsColMfrOverride).Width = 16.64;
        ws.Column(WsColQtyOverride).Width = 16.64;
        ws.Column(WsColQtyOverride).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Column(WsColQtyOverride).Style.NumberFormat.Format = "0";

        // Conditional formatting: overridden cells render bold + red so the pricing team
        // can see at a glance which Mfr/Qty values are user-authored vs Revit-sourced.
        if (lastDataRow >= 2)
        {
            ws.Range(2, WsColMfrOverride, lastDataRow, WsColMfrOverride)
                .AddConditionalFormat()
                .WhenIsTrue($"LEN(U2)>0")
                .Font.SetBold().Font.SetFontColor(XLColor.Red);
            ws.Range(2, WsColQtyOverride, lastDataRow, WsColQtyOverride)
                .AddConditionalFormat()
                .WhenIsTrue($"LEN(V2)>0")
                .Font.SetBold().Font.SetFontColor(XLColor.Red);
        }

        ApplyPricingColumnFormats(ws);

        // Hide gridlines so only the explicit type-group dividers read as separators
        ws.ShowGridLines = false;
        ApplyStandardPageSetup(ws);

        // Borders, row heights, banding — col G (already styled by ApplyWorksheetTypography
        // above) acts as the visual separator between the locked Revit-side block (A–F)
        // and the editable pricing block (H–V).
        ApplyWorksheetRowHeights(ws);
        ApplyWorksheetBorders(ws, lastDataRow);
        ApplyAltRowFill(ws, lastDataRow);

        // Dark divider at the last row of each Type group (cuts across col G + the pricing block border)
        ApplyTypeGroupDividers(ws, 2, row - 1);

        // Helper pipeline (Z hidden flag already written per-row; AA-BJ spill formulas here)
        WriteHelperPipeline(ws, lastDataRow);

        // Hide helper columns Z..BJ
        for (int col = WsColActive; col <= WsColHelperLast; col++)
            ws.Column(col).Hide();

        // Sheet protection — lock TurboSuite columns (A-F), unlock user columns (G-U).
        // Exception: per-Type canonical fields — Tariff % (K) and Notes (N–S) — are only
        // editable on each Type's canonical (first) row.
        var typeCanonicalRows = new HashSet<int>(typeFirstRow.Values);
        for (int r = 2; r < row; r++)
        {
            for (int col = WsColDesc; col <= WsColQtyOverride; col++)
                ws.Cell(r, col).Style.Protection.SetLocked(false);
            if (!typeCanonicalRows.Contains(r))
            {
                ws.Cell(r, WsColTariff).Style.Protection.SetLocked(true);
                for (int n = 0; n < 6; n++)
                    ws.Cell(r, WsColNote1 + n).Style.Protection.SetLocked(true);
            }
        }
        ws.Protect().AllowElement(XLSheetProtectionElements.FormatColumns);
    }

    // Currency/percent number formats for the pricing columns. Applied from both build and
    // update paths because update clears row 2's cell-level formats — falling back to column
    // style isn't reliable once cells have been written with numeric values.
    private static void ApplyPricingColumnFormats(IXLWorksheet ws)
    {
        ws.Column(WsColUnitCost).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(WsColMarkup).Style.NumberFormat.Format = "0%";
        ws.Column(WsColTariff).Style.NumberFormat.Format = "0%";
        ws.Column(WsColAdder).Style.NumberFormat.Format = "$#,##0.00";

        // Center Markup / Tariff / Adder / Phase (header + data) — short numeric values read
        // better centered, and the trimmed "Markup"/"Tariff" headers no longer fit cleanly left-aligned.
        ws.Column(WsColMarkup).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Column(WsColTariff).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Column(WsColAdder).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Column(WsColPhase).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    // Width/alignment/number-format for Qty, Prev, Δ. Applied from both build and update
    // paths — update clears row formats, so per-cell alignment wouldn't survive a round-trip.
    private static void ApplyQtyColumnFormatting(IXLWorksheet ws)
    {
        ws.Column(WsColQty).Width = 6;
        ws.Column(WsColPrevQty).Width = 6;
        ws.Column(WsColDelta).Width = 6;
        ws.Column(WsColQty).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Column(WsColPrevQty).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Column(WsColDelta).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Column(WsColDelta).Style.NumberFormat.Format = "+0;-0;;@";
    }

    private static void ApplyTypeGroupDividers(IXLWorksheet ws, int firstRow, int lastRow)
    {
        for (int r = firstRow; r < lastRow; r++)
        {
            string thisType = ws.Cell(r, WsColType).GetString();
            string nextType = ws.Cell(r + 1, WsColType).GetString();
            if (string.Equals(thisType, nextType, StringComparison.OrdinalIgnoreCase))
                continue;

            var rng = ws.Range(r, WsColType, r, WsColQtyOverride);
            rng.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            rng.Style.Border.BottomBorderColor = XLColor.FromHtml("#262626");
        }
    }

    // Worksheet header strip: uniform #262626 background, bold, with per-section font colors.
    // A–F (Revit-side) and G (Type repeat) = #A6A6A6; H–T (pricing inputs) = #FACC75;
    // U–V (overrides) = red, matching the override-value CF.
    private static void ApplyWorksheetHeaderStyling(IXLWorksheet ws)
    {
        var headerRange = ws.Range(1, WsColType, 1, WsColQtyOverride);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#262626");

        ws.Range(1, WsColType, 1, WsColDelta).Style.Font.FontColor = XLColor.FromHtml("#A6A6A6");
        ws.Cell(1, WsColTypeRepeat).Style.Font.FontColor = XLColor.FromHtml("#A6A6A6");
        ws.Range(1, WsColDesc, 1, WsColNote6).Style.Font.FontColor = XLColor.FromHtml("#FACC75");
        ws.Range(1, WsColMfrOverride, 1, WsColQtyOverride).Style.Font.FontColor = XLColor.Red;
    }

    // Whole-sheet font Segoe UI 12, with col G overridden to size 11 italic gray right-aligned
    // to mirror the Type value as a thin visual separator between locked and pricing blocks.
    private static void ApplyWorksheetTypography(IXLWorksheet ws)
    {
        ws.Style.Font.FontName = "Segoe UI";
        ws.Style.Font.FontSize = 12;

        var typeRepeat = ws.Column(WsColTypeRepeat);
        typeRepeat.Style.Font.FontName = "Segoe UI";
        typeRepeat.Style.Font.FontSize = 11;
        typeRepeat.Style.Font.Italic = true;
        typeRepeat.Style.Font.FontColor = XLColor.FromHtml("#A6A6A6");
        typeRepeat.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        typeRepeat.AdjustToContents();

        // Header row 1 keeps Segoe UI 12 (sheet default) plus bold + per-section colors set
        // by ApplyWorksheetHeaderStyling. Re-apply the gray font on G1 so the column-level
        // override doesn't leak italic onto a header cell that should stay blank.
        ws.Cell(1, WsColTypeRepeat).Style.Font.Italic = false;
        ws.Cell(1, WsColTypeRepeat).Style.Font.FontSize = 12;
    }

    private static void ApplyWorksheetRowHeights(IXLWorksheet ws)
    {
        ws.RowHeight = 17.5;
        ws.Row(1).Height = 45;
    }

    // Per-row literal #F2F2F2 fill on every other data row, skipping col G entirely so the
    // visual gap between the Revit-side block (A–F) and the pricing block (H–V) reads cleanly.
    // Pricing team can't add rows; new rows only come from Revit-side updates which always
    // re-run the build/update path and re-apply this banding from scratch.
    private static void ApplyAltRowFill(IXLWorksheet ws, int lastDataRow)
    {
        if (lastDataRow < 3) return;
        var fill = XLColor.FromHtml("#F2F2F2");
        for (int r = 3; r <= lastDataRow; r += 2)
        {
            ws.Range(r, WsColType, r, WsColDelta).Style.Fill.BackgroundColor = fill;
            ws.Range(r, WsColDesc, r, WsColQtyOverride).Style.Fill.BackgroundColor = fill;
        }
    }

    // Thin #D9D9D9 grid inside both data blocks (A2:F{last} and H2:V{last}), medium black
    // outer border around each block. Headers excluded — header strip stands on its own
    // via the dark #262626 fill. Col G has no borders (visual gap by design).
    private static void ApplyWorksheetBorders(IXLWorksheet ws, int lastDataRow)
    {
        if (lastDataRow < 2) return;
        var inside = XLColor.FromHtml("#D9D9D9");

        var lockedBlock = ws.Range(2, WsColType, lastDataRow, WsColDelta);
        lockedBlock.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        lockedBlock.Style.Border.InsideBorderColor = inside;
        lockedBlock.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        lockedBlock.Style.Border.OutsideBorderColor = XLColor.Black;

        var pricingBlock = ws.Range(2, WsColDesc, lastDataRow, WsColQtyOverride);
        pricingBlock.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        pricingBlock.Style.Border.InsideBorderColor = inside;
        pricingBlock.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        pricingBlock.Style.Border.OutsideBorderColor = XLColor.Black;
    }

    // C# mirror of BuildQtyFormula for recomputing Qty when the Excel-side cached value
    // isn't available (used by the PrevQty fallback path on 3rd+ update passes).
    private static double ComputeQtyForCalc(string? calc, CountsFixtureModel f)
    {
        double linearPadded = Math.Ceiling(f.LinearLength * 1.05);
        return calc switch
        {
            "Reel"    => f.ReelLength > 0    ? Math.Ceiling(linearPadded / Math.Ceiling(f.ReelLength)) : f.Count,
            "Channel" => f.ChannelLength > 0 ? Math.Ceiling(linearPadded / Math.Ceiling(f.ChannelLength)) : f.Count,
            "Clip"    => Math.Ceiling(linearPadded / 1.75),
            "End Cap" => f.Count,
            _         => f.Count,
        };
    }

    private static string BuildQtyFormula(int row, string csRef)
    {
        // VLOOKUP indices: Col 9=Count, Col 10=LinearLength, Col 11=ReelLength, Col 12=ChannelLength
        return $"IF(I{row}=\"Reel\"," +
               $"CEILING(CEILING(VLOOKUP(A{row},{csRef}!A:L,10,FALSE)*1.05,1)/CEILING(VLOOKUP(A{row},{csRef}!A:L,11,FALSE),1),1)," +
               $"IF(I{row}=\"Channel\"," +
               $"CEILING(CEILING(VLOOKUP(A{row},{csRef}!A:L,10,FALSE)*1.05,1)/CEILING(VLOOKUP(A{row},{csRef}!A:L,12,FALSE),1),1)," +
               $"IF(I{row}=\"End Cap\"," +
               $"VLOOKUP(A{row},{csRef}!A:L,9,FALSE)," +
               $"IF(I{row}=\"Clip\"," +
               $"CEILING(CEILING(VLOOKUP(A{row},{csRef}!A:L,10,FALSE)*1.05,1)/1.75,1)," +
               $"VLOOKUP(A{row},{csRef}!A:L,9,FALSE)))))";
    }

    /// <summary>
    /// Writes the AA-BJ helper column pipeline — one spill formula per final print-output column.
    /// Quote (AA-AI) + Phase 1/2/3 (AK-AR, AT-BA, BC-BJ). Each column's formula:
    /// FILTER data rows by predicate → inline LAMBDA injects gap rows (type-group boundaries) and
    /// tariff rows (per-Type line item at end of each group) → VSTACK appends Subtotal/Freight/
    /// Grand Total footer to Sell Ea. (labels) and Sell Ext. (amounts). Print sheets consume
    /// via ANCHORARRAY.
    /// </summary>
    private static void WriteHelperPipeline(IXLWorksheet ws, int lastDataRow)
    {
        if (lastDataRow < 2)
        {
            // No data — clear any stale helper formulas and bail.
            for (int col = WsColHelperLast; col >= WsColActive + 1; col--)
                ws.Cell(2, col).Clear(XLClearOptions.AllContents);
            return;
        }

        // Active flag lives at WsColActive (AG after the Type-repeat column shift). Every spill
        // predicate filters on AG=1 to exclude strikethrough rows. Phase column is N.
        WriteSingleHelperPipeline(ws, lastDataRow, QuoteHelperCols,
            predicate: $"(AG2:AG{lastDataRow}=1)", includeDelta: true);
        WriteSingleHelperPipeline(ws, lastDataRow, Phase1HelperCols,
            predicate: $"(AG2:AG{lastDataRow}=1)*(N2:N{lastDataRow}=1)", includeDelta: false);
        WriteSingleHelperPipeline(ws, lastDataRow, Phase2HelperCols,
            predicate: $"(AG2:AG{lastDataRow}=1)*(N2:N{lastDataRow}=2)", includeDelta: false);
        WriteSingleHelperPipeline(ws, lastDataRow, Phase3HelperCols,
            predicate: $"(AG2:AG{lastDataRow}=1)*(N2:N{lastDataRow}=3)", includeDelta: false);
    }

    private static void WriteSingleHelperPipeline(
        IXLWorksheet ws, int lastDataRow, string[] cols, string predicate, bool includeDelta)
    {
        // Per-row array expressions (unfiltered; FILTER applied inside Gap).
        string Col(string c) => $"{c}2:{c}{lastDataRow}";
        // Effective Mfr/Qty: override column wins when populated, else fall back to Revit.
        // effQty reads the BR column (populated row-by-row with IF(V="",D,V)) so callers
        // don't duplicate the fallback logic.
        string effMfr   = $"IF({Col("U")}=\"\",{Col("B")},{Col("U")})";
        string effQty   = Col("BR");
        string effDelta = $"IF({Col("E")}=\"\",\"\",{effQty}-{Col("E")})";
        string sellEa = $"IFERROR(({Col("J")}*(1+{Col("K")}))+{Col("M")},0)";
        string sellExt = $"({sellEa})*{effQty}";
        // Coerce to number (*1) so text placeholders like "dependent" and blanks
        // fall through IFERROR to 0 instead of propagating as a string into Buy Ext.
        string buyEa = $"IFERROR({Col("J")}*1,0)";
        string buyExt = $"({buyEa})*{effQty}";
        // Tariff base = Sell Ext. (includes Adder). Prior version omitted M, underpricing tariffs.
        string tariffBasePerRow = sellExt;
        // Exclude "dependent" placeholder — it's a visual cue on Worksheet for drag-fill links,
        // not a real description. Treated as blank here so it doesn't leak into print sheets.
        string catalogCombined =
            $"{Col("C")}&IF(({Col("H")}<>0)*({Col("H")}<>\"\")*({Col("H")}<>\"dependent\"),\" ~ \"&{Col("H")},\"\")";

        // Gap LAMBDA: emits gap rows at type-group boundaries, a "Tariff" row, and up to six
        // per-Type NOTE rows at each group's end. Inline LAMBDA — defined-name LAMBDAs called
        // from spill cells trip Excel's load-time parser.
        // Per-row type tariff %: XLOOKUP against the full A/L ranges returns the first match,
        // which is the Type's canonical row (where the literal L value lives). Non-canonical
        // rows have L blank, so only the canonical is found. Wrapped in IFERROR to coerce a
        // blank canonical L to 0 so arithmetic downstream stays numeric.
        string typeKPerRow = $"IFERROR(_xlfn.XLOOKUP({Col("A")},{Col("A")},{Col("L")}),0)";
        // Per-row type Note_n: same XLOOKUP pattern against each note column (O–T). Blank
        // canonical cells make XLOOKUP return the number 0 (not ""), so we wrap in LET and
        // coerce any numeric result back to "" — otherwise the downstream gate (_xlpm.n<>"")
        // would fire on every row because 0<>"" is TRUE.
        string[] noteCols = { "O", "P", "Q", "R", "S", "T" };
        string NotePerRow(string noteCol) =>
            $"IFERROR(_xlfn.LET(_xlpm.v,_xlfn.XLOOKUP({Col("A")},{Col("A")},{Col(noteCol)}),IF(_xlpm.v=0,\"\",_xlpm.v)),\"\")";
        string typesArg = $"_xlfn._xlws.FILTER({Col("A")},{predicate})";
        string pctsArg = $"_xlfn._xlws.FILTER({typeKPerRow},{predicate})";
        string baseArg = $"_xlfn._xlws.FILTER({tariffBasePerRow},{predicate})";
        string[] noteArgs = noteCols
            .Select(nc => $"_xlfn._xlws.FILTER({NotePerRow(nc)},{predicate})")
            .ToArray();

        // Content expressions for the 6 note slots, one per caller. Inside the LAMBDA, the
        // note values are bound to _xlpm.n1.._xlpm.n6. Slots emit only when isLast AND the
        // corresponding note is non-empty.
        string Gap(string valsExpr, string tariffContentExpr, string[] noteContentExprs)
        {
            if (noteContentExprs.Length != 6)
                throw new ArgumentException("Expected 6 note content expressions", nameof(noteContentExprs));
            string valsArg = $"_xlfn._xlws.FILTER({valsExpr},{predicate})";
            string noteLetCols = string.Join(",", Enumerable.Range(1, 6).Select(i =>
                $"_xlpm.n{i}Col,IF(_xlpm.isLast*(_xlpm.n{i}<>\"\"),{noteContentExprs[i - 1]},_xlfn.NA())"));
            string hstackCols = "_xlpm.gapCol,_xlpm.vals,_xlpm.tariffCol,"
                + string.Join(",", Enumerable.Range(1, 6).Select(i => $"_xlpm.n{i}Col"));
            return "_xlfn.LAMBDA(_xlpm.types,_xlpm.vals,_xlpm.pcts,_xlpm.base,"
                 +   "_xlpm.n1,_xlpm.n2,_xlpm.n3,_xlpm.n4,_xlpm.n5,_xlpm.n6,"
                 +   "IF(ROWS(_xlpm.vals)<=1,_xlpm.vals,"
                 +     "_xlfn.LET("
                 +       "_xlpm.prev,_xlfn.VSTACK(INDEX(_xlpm.types,1),_xlfn.DROP(_xlpm.types,-1)),"
                 +       "_xlpm.nxt,_xlfn.VSTACK(_xlfn.DROP(_xlpm.types,1),\"\"),"
                 +       "_xlpm.gapCol,IF(_xlpm.types<>_xlpm.prev,\"\",_xlfn.NA()),"
                 +       "_xlpm.isLast,_xlpm.types<>_xlpm.nxt,"
                 +       "_xlpm.totals,_xlfn.BYROW(_xlpm.types,_xlfn.LAMBDA(_xlpm.tv,SUMPRODUCT((_xlpm.types=_xlpm.tv)*_xlpm.base))),"
                 +       $"_xlpm.tariffCol,IF(_xlpm.isLast*(_xlpm.pcts<>0),{tariffContentExpr},_xlfn.NA()),"
                 +       noteLetCols + ","
                 +       $"_xlfn.TOCOL(_xlfn.HSTACK({hstackCols}),2)"
                 +     ")"
                 +   ")"
                 + $")({typesArg},{valsArg},{pctsArg},{baseArg},{string.Join(",", noteArgs)})";
        }

        // Note content expressions per print-column. Most columns leave note rows blank;
        // only Type (blank), Mfr ("NOTE:"), Catalog (the note text itself), and InDataBlock
        // flag (1) carry values into note rows.
        string[] NoteBlank() => Enumerable.Repeat("\"\"", 6).ToArray();
        string[] NoteLabel() => Enumerable.Repeat("\"NOTE:\"", 6).ToArray();
        string[] NoteText() => new[] { "_xlpm.n1", "_xlpm.n2", "_xlpm.n3", "_xlpm.n4", "_xlpm.n5", "_xlpm.n6" };

        // Sell subtotal = Σ(filtered line items) + Σ(filtered per-row tariff allocation).
        // K is only populated on each Type's canonical row, so we use the per-row XLOOKUP
        // resolver (typeKPerRow) to broadcast the type's tariff % onto every row in the group.
        string sellSubtotal =
            $"SUMPRODUCT(({predicate})*{sellEa}*{effQty})"
            + $"+SUMPRODUCT(({predicate})*{tariffBasePerRow}*{typeKPerRow})";
        // Buy subtotal = Σ(filtered Unit Cost * EffQty). No tariff on Buy side.
        // Uses the coerced buyEa (not raw Col("I")) so text placeholders like "dependent"
        // contribute 0 instead of #VALUE-poisoning the whole subtotal.
        string buySubtotal = $"SUMPRODUCT(({predicate})*{buyEa}*{effQty})";

        // Quote footer notes — appended to the Type column under the Grand Total line.
        // FILTER drops empty rows so users can fill fewer than 15 notes without blank spill.
        string notesSpill = "_xlfn._xlws.FILTER(QuoteNotes,QuoteNotes<>\"\",\"\")";

        // Footer labels live on the Qty column and serve both Buy Ext. and Sell Ext.
        // Lutron row is omitted when Dashboard!LutronSubtotal is blank.
        string labelFooter =
            "IF(LutronSubtotal=\"\","
            + "_xlfn.VSTACK(\"\",\"Fixture Package Sub-Total:\",\"Estimated Freight:\",\"LIGHTING PACKAGE TOTAL:\"),"
            + "_xlfn.VSTACK(\"\",\"Fixture Package Sub-Total:\",\"Lutron Lighting Control Sub-Total:\",\"Estimated Freight:\",\"LIGHTING PACKAGE TOTAL:\"))";

        // Sell Ext. footer values (tariff row carries per-type tariff amount).
        string sellValueFooter =
            "IF(LutronSubtotal=\"\","
            + $"_xlfn.VSTACK(\"\",{sellSubtotal},FreightSell,{sellSubtotal}+FreightSell),"
            + $"_xlfn.VSTACK(\"\",{sellSubtotal},LutronSubtotal,FreightSell,{sellSubtotal}+LutronSubtotal+FreightSell))";

        // Buy Ext. footer values (no tariff on Buy side).
        string buyValueFooter =
            "IF(LutronSubtotal=\"\","
            + $"_xlfn.VSTACK(\"\",{buySubtotal},FreightBuy,{buySubtotal}+FreightBuy),"
            + $"_xlfn.VSTACK(\"\",{buySubtotal},LutronSubtotal,FreightBuy,{buySubtotal}+LutronSubtotal+FreightBuy))";

        int i = 0;
        // Type — blank tariff row, blank note rows, plus quote footer notes appended at the bottom
        ws.Cell($"{cols[i++]}2").FormulaA1 =
            $"_xlfn.VSTACK(IFERROR({Gap(Col("A"), "\"\"", NoteBlank())},\"\"),\"\",\"\",\"\",\"\",\"\",{notesSpill})";
        // Mfr — tariff row carries the type's Mfr (vals = effMfr per-row, isLast picks the
        // last row of each group); "NOTE:" label on note rows; uses effMfr so override wins per-row
        ws.Cell($"{cols[i++]}2").FormulaA1 = $"IFERROR({Gap(effMfr, "_xlpm.vals", NoteLabel())},\"\")";
        // Catalog~Desc — "Tariff …" label on tariff row; note text on note rows
        ws.Cell($"{cols[i++]}2").FormulaA1 = $"IFERROR({Gap(catalogCombined, "\"Tariff *may be deleted/reduced if tariffs change\"", NoteText())},\"\")";
        // Qty + footer labels (Subtotal / [Lutron?] / Freight / Grand Total).
        ws.Cell($"{cols[i++]}2").FormulaA1 =
            $"_xlfn.VSTACK(IFERROR({Gap(effQty, "\"\"", NoteBlank())},\"\"),{labelFooter})";
        // Delta (Quote only) — blank tariff row, blank note rows, no footer. Uses effDelta so
        // the print-sheet delta reflects the price being quoted; Worksheet's col F stays wired
        // to Revit D−E.
        if (includeDelta)
            ws.Cell($"{cols[i++]}2").FormulaA1 = $"IFERROR({Gap(effDelta, "\"\"", NoteBlank())},\"\")";
        // Buy Ea. — blank tariff row, blank note rows, no footer rows
        ws.Cell($"{cols[i++]}2").FormulaA1 = $"IFERROR({Gap(buyEa, "\"\"", NoteBlank())},\"\")";
        // Buy Ext. + footer values
        ws.Cell($"{cols[i++]}2").FormulaA1 =
            $"_xlfn.VSTACK(IFERROR({Gap(buyExt, "\"\"", NoteBlank())},\"\"),{buyValueFooter})";
        // Sell Ea. — blank tariff row, blank note rows, no footer rows (labels live on Qty)
        ws.Cell($"{cols[i++]}2").FormulaA1 = $"IFERROR({Gap(sellEa, "\"\"", NoteBlank())},\"\")";
        // Sell Ext. + footer values (tariff row carries per-type tariff amount; note rows blank)
        ws.Cell($"{cols[i++]}2").FormulaA1 =
            $"_xlfn.VSTACK(IFERROR({Gap(sellExt, "_xlpm.totals*_xlpm.pcts", NoteBlank())},\"\"),{sellValueFooter})";
        // InDataBlock flag — 1 for every data row, type-gap row, tariff row, and note row;
        // blank for footer/quote-notes rows (no VSTACK append). Mirrors Gap's structural shape
        // so the flag column aligns row-for-row with the visible helper columns on the print
        // sheets. The print sheet drives its border CF against this flag ($flagCol{row}=1).
        string noteFlagLet = string.Join(",", Enumerable.Range(1, 6).Select(n =>
            $"_xlpm.n{n}Col,IF(_xlpm.isLast*(_xlpm.n{n}<>\"\"),1,_xlfn.NA())"));
        string flagHstackCols = "_xlpm.gapCol,_xlpm.valsCol,_xlpm.tariffCol,"
            + string.Join(",", Enumerable.Range(1, 6).Select(n => $"_xlpm.n{n}Col"));
        string flagLambda =
              "_xlfn.LAMBDA(_xlpm.types,_xlpm.pcts,_xlpm.n1,_xlpm.n2,_xlpm.n3,_xlpm.n4,_xlpm.n5,_xlpm.n6,"
            +   "IF(ROWS(_xlpm.types)<=1,1,"
            +     "_xlfn.LET("
            +       "_xlpm.prev,_xlfn.VSTACK(INDEX(_xlpm.types,1),_xlfn.DROP(_xlpm.types,-1)),"
            +       "_xlpm.nxt,_xlfn.VSTACK(_xlfn.DROP(_xlpm.types,1),\"\"),"
            +       "_xlpm.gapCol,IF(_xlpm.types<>_xlpm.prev,1,_xlfn.NA()),"
            +       "_xlpm.isLast,_xlpm.types<>_xlpm.nxt,"
            +       "_xlpm.valsCol,_xlfn.SEQUENCE(ROWS(_xlpm.types),1,1,0),"
            +       "_xlpm.tariffCol,IF(_xlpm.isLast*(_xlpm.pcts<>0),1,_xlfn.NA()),"
            +       noteFlagLet + ","
            +       $"_xlfn.TOCOL(_xlfn.HSTACK({flagHstackCols}),2)"
            +     ")"
            +   ")"
            + $")({typesArg},{pctsArg},{string.Join(",", noteArgs)})";
        ws.Cell($"{cols[i++]}2").FormulaA1 = $"IFERROR({flagLambda},\"\")";
    }

    #endregion

    #region Print Sheets (Quote, Phase 1/2/3)

    /// <summary>
    /// Builds the Quote print sheet as a thin consumer of Worksheet helper columns AA-AG.
    /// 7 single-cell ANCHORARRAY formulas at row 7. All pipeline logic (filter, gap rows,
    /// tariff rows, subtotal/freight/grand-total footer) lives on Worksheet — this sheet is
    /// print formatting only.
    /// </summary>
    private static void RebuildQuoteAndPhaseSheets(IXLWorkbook wb)
    {
        var names = new[] { "Quote", "Phase 1", "Phase 2", "Phase 3" };
        var positions = new Dictionary<string, int>();
        foreach (var name in names)
        {
            if (wb.Worksheets.TryGetWorksheet(name, out var existing))
            {
                positions[name] = existing.Position;
                existing.Delete();
            }
        }
        BuildQuoteSheet(wb);
        for (int p = 1; p <= 3; p++)
            BuildPhaseQuoteSheet(wb, p);
        // Restore positions. Insert each sheet at the anchor (smallest saved
        // position) in reverse name order — each insertion pushes previously
        // placed sheets one slot right, yielding Quote|Phase 1|Phase 2|Phase 3.
        if (positions.Count > 0)
        {
            int anchor = positions.Values.Min();
            foreach (var name in names.Reverse())
            {
                if (positions.ContainsKey(name)
                    && wb.Worksheets.TryGetWorksheet(name, out var ws))
                {
                    ws.Position = anchor;
                }
            }
        }
    }

    private static void BuildQuoteSheet(IXLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Quote");
        if (!wb.Worksheets.TryGetWorksheet("Worksheet", out var wsSheet))
            return;

        ApplyPrintSheetDefaults(ws);

        // Subtitle includes a second line indicating what the Δ column compares against —
        // either the latest prior run (default) or the user-selected Reference Counts date.
        WritePrintSheetTitle(ws, 9,
            "\"PRODUCT PRICING \"&Cover!B11&CHAR(10)&" +
            "IF(BidDate=\"\",\"Δ vs. last run\",\"Δ vs. \"&TEXT(BidDate,\"yyyy-mm-dd\"))");
        ws.Cell("A2").Style.Alignment.WrapText = true;
        ws.Row(2).Height = 30;

        int headerRow = 6;
        string[] headers = { " Type", "Mfr", "Catalog Number", "Qty", "Δ", "Buy Ea.", "Buy Ext.", "Sell Ea.", "Sell Ext." };
        WritePrintSheetHeaders(ws, headerRow, headers);

        // Spill row shifted to 8 (row 7 is a blank spacer). QuoteHelperCols has 10 entries —
        // the last is the InDataBlock flag, spilled into hidden column J.
        int spillRow = 8;
        for (int i = 0; i < QuoteHelperCols.Length; i++)
        {
            string anchor = $"_xlfn.ANCHORARRAY(Worksheet!{QuoteHelperCols[i]}2)";
            string formula = i switch
            {
                0 => BuildLeadingSpaceFormula(anchor),
                1 => BuildMfrDisplayFormula(anchor),
                _ => anchor,
            };
            ws.Cell(spillRow, i + 1).FormulaA1 = formula;
        }
        ws.Column(10).Hide(); // InDataBlock flag — drives border CF only

        ApplyNotesBoldConditionalFormat(ws, spillRow);
        ApplyDataBorderConditionalFormat(ws, spillRow, lastVisibleCol: 9, flagColLetter: "J");
        ApplyFooterStyling(ws, spillRow, qtyCol: 4, buyExtCol: 7, sellExtCol: 9);
        ws.Rows(spillRow, 1000).Height = 15.5;

        // Currency + delta formats
        ws.Column(5).Style.NumberFormat.Format = "+0;-0;;@";
        ws.Column(6).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(7).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(8).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(9).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        // Right-align Qty — numbers and footer labels both; labels overflow left into the
        // (empty) Catalog column on footer rows, which is intentional.
        ws.Column(4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws.Column(5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        for (int c = 6; c <= 9; c++)
            ws.Column(c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        // Qty and Δ header cells centered (column-level right-align applies to data rows only)
        ws.Cell(headerRow, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(headerRow, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        // Column widths — pull from Worksheet (ANCHORARRAY cells can't auto-size).
        ws.Column(1).Width = Math.Max(6.25, wsSheet.Column(WsColType).Width);
        ws.Column(2).Width = ComputeMfrDisplayWidth(wsSheet);
        ws.Column(3).Width = ComputeCombinedCatalogWidth(wsSheet);
        // Long note rows wrap inside the catalog column; row height grows automatically.
        ws.Column(3).Style.Alignment.WrapText = true;
        ws.Column(4).Width = 8; // Qty — sized for numeric data only; footer labels spill left
        ws.Column(5).Width = 6;
        ws.Column(6).Width = 12;
        ws.Column(7).Width = 12;
        ws.Column(8).Width = 12;
        ws.Column(9).Width = 12;

        // Print setup
        ApplyStandardPageSetup(ws);
        ws.PageSetup.SetRowsToRepeatAtTop(1, 7);
    }

    /// <summary>
    /// Builds a Phase print sheet as a thin consumer of its Worksheet helper column block.
    /// 6 single-cell ANCHORARRAY formulas at row 7.
    /// </summary>
    private static void BuildPhaseQuoteSheet(IXLWorkbook wb, int phase)
    {
        string sheetName = $"Phase {phase}";
        var ws = wb.Worksheets.Add(sheetName);
        if (!wb.Worksheets.TryGetWorksheet("Worksheet", out var wsSheet))
            return;

        string[] cols = phase switch
        {
            1 => Phase1HelperCols,
            2 => Phase2HelperCols,
            3 => Phase3HelperCols,
            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };

        ApplyPrintSheetDefaults(ws);

        WritePrintSheetTitle(ws, 8, $"\"PHASE {phase} PRODUCT PRICING \"&Cover!B11");

        int headerRow = 6;
        string[] headers = { " Type", "Mfr", "Catalog Number", "Qty", "Buy Ea.", "Buy Ext.", "Sell Ea.", "Sell Ext." };
        WritePrintSheetHeaders(ws, headerRow, headers);

        // Spill row shifted to 8 (row 7 is a blank spacer). Phase col arrays have 9 entries —
        // the last is the InDataBlock flag, spilled into hidden column I.
        int spillRow = 8;
        for (int i = 0; i < cols.Length; i++)
        {
            string anchor = $"_xlfn.ANCHORARRAY(Worksheet!{cols[i]}2)";
            string formula = i switch
            {
                0 => BuildLeadingSpaceFormula(anchor),
                1 => BuildMfrDisplayFormula(anchor),
                _ => anchor,
            };
            ws.Cell(spillRow, i + 1).FormulaA1 = formula;
        }
        ws.Column(9).Hide(); // InDataBlock flag — drives border CF only

        ApplyNotesBoldConditionalFormat(ws, spillRow);
        ApplyDataBorderConditionalFormat(ws, spillRow, lastVisibleCol: 8, flagColLetter: "I");
        ApplyFooterStyling(ws, spillRow, qtyCol: 4, buyExtCol: 6, sellExtCol: 8);
        ws.Rows(spillRow, 1000).Height = 15.5;

        // Currency formats
        ws.Column(5).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(6).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(7).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(8).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        // Right-align Qty — numbers and footer labels both; labels overflow left into the
        // (empty) Catalog column on footer rows, which is intentional.
        ws.Column(4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        for (int c = 5; c <= 8; c++)
            ws.Column(c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        // Qty header cell centered (column-level right-align applies to data rows only)
        ws.Cell(headerRow, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        // Column widths
        ws.Column(1).Width = Math.Max(6.25, wsSheet.Column(WsColType).Width);
        ws.Column(2).Width = ComputeMfrDisplayWidth(wsSheet);
        ws.Column(3).Width = ComputeCombinedCatalogWidth(wsSheet);
        // Long note rows wrap inside the catalog column; row height grows automatically.
        ws.Column(3).Style.Alignment.WrapText = true;
        ws.Column(4).Width = 8; // Qty — sized for numeric data only; footer labels spill left
        ws.Column(5).Width = 12;
        ws.Column(6).Width = 12;
        ws.Column(7).Width = 12;
        ws.Column(8).Width = 12;

        // Print setup
        ApplyStandardPageSetup(ws);
        ws.PageSetup.SetRowsToRepeatAtTop(1, 7);
    }

    /// <summary>Sets print-sheet-wide defaults: Segoe UI 11 font, gridlines off.</summary>
    private static void ApplyPrintSheetDefaults(IXLWorksheet ws)
    {
        ws.ShowGridLines = false;
        ws.Style.Font.FontName = "Segoe UI";
        ws.Style.Font.FontSize = 11;
        ws.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    /// <summary>
    /// Builds the Mfr-column spill formula for Quote/Phase print sheets. Uppercases every
    /// value, then applies hardcoded display substitutions (e.g. Environmental Lights →
    /// LUMEN SPEC). IF returns an array when its arguments are arrays, so the spill is
    /// preserved. Comparison is case-insensitive because UPPER is applied first.
    /// </summary>
    private static string BuildMfrDisplayFormula(string anchor)
    {
        string upper = $"UPPER({anchor})";
        return $"IF({upper}=\"ENVIRONMENTAL LIGHTS\",\"LUMEN SPEC\",{upper})";
    }

    /// <summary>Prepends a single space to each spilled value for visual left-padding on a
    /// text column. Empty spill cells stay empty so the border CF's gap rows don't get turned
    /// into single-space cells.</summary>
    private static string BuildLeadingSpaceFormula(string anchor)
    {
        return $"IF({anchor}=\"\",\"\",\" \"&{anchor})";
    }

    /// <summary>
    /// Applies conditional formatting to the Type column (A) of a print sheet so notes flagged
    /// BOLD on the Dashboard render bold. Notes spill into column A after the footer rows via
    /// FILTER(QuoteNotes) — their position is dynamic, so we broadcast CF over a generous range
    /// and match each cell's text against QuoteNotes/QuoteNotesBold by INDEX/MATCH. Bolding
    /// assumes note text is unique; identical notes with different bold flags will all bold.
    /// </summary>
    private static void ApplyNotesBoldConditionalFormat(IXLWorksheet ws, int spillRow)
    {
        string firstCell = $"A{spillRow}";
        ws.Range($"{firstCell}:A1000")
            .AddConditionalFormat()
            .WhenIsTrue($"IFERROR(INDEX(QuoteNotesBold,MATCH({firstCell},QuoteNotes,0))=TRUE,FALSE)")
            .Font.SetBold();
    }

    // Print sheet border styling — Medium #808080 used for data-row borders, banner borders,
    // header borders, and footer subtotal/grand-total top borders.
    private static readonly XLColor PrintBorderColor = XLColor.Black;
    private const XLBorderStyleValues PrintBorderStyle = XLBorderStyleValues.Thin;

    /// <summary>Writes the 7-row title block (header at row 6, spacer at row 7) shared by
    /// Quote and Phase sheets. Row 3 is a thin spacer, row 7 is a larger spacer that separates
    /// the title block from spilled data. Fonts are Segoe UI inherited from sheet defaults.</summary>
    private static void WritePrintSheetTitle(IXLWorksheet ws, int mergeColCount, string subtitleFormula)
    {
        string lastCol = XLHelper.GetColumnLetterFromNumber(mergeColCount);

        // Row 1 — project title (no fill, black, Segoe UI 12 bold, centered, no border)
        ws.Range($"A1:{lastCol}1").Merge();
        ws.Cell("A1").FormulaA1 = "Cover!B6";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 12;
        ws.Cell("A1").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Row(1).Height = 15;

        // Row 2 — subtitle (Segoe UI 11, not bold, centered, no fill, no border)
        ws.Range($"A2:{lastCol}2").Merge();
        ws.Cell("A2").FormulaA1 = subtitleFormula;
        ws.Cell("A2").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Row(2).Height = 15;

        // Row 3 — thin spacer
        ws.Row(3).Height = 4;

        // Row 4 — substitutions banner (yellow fill, black bold, Medium border)
        ws.Range($"A4:{lastCol}4").Merge();
        ws.Cell("A4").Value = "ANY SUBSTITUTIONS MUST BE APPROVED BY CDLTG";
        ws.Cell("A4").Style.Font.Bold = true;
        ws.Cell("A4").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell("A4").Style.Fill.BackgroundColor = YellowFill;
        ApplyBannerBorder(ws.Range($"A4:{lastCol}4"));

        // Row 5 — 5-day banner (black fill, yellow text, bold, Medium border)
        ws.Range($"A5:{lastCol}5").Merge();
        ws.Cell("A5").Value = "ALL PRICING BELOW IS VALID FOR 5 BUSINESS DAYS";
        ws.Cell("A5").Style.Font.Bold = true;
        ws.Cell("A5").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell("A5").Style.Fill.BackgroundColor = XLColor.Black;
        ws.Cell("A5").Style.Font.FontColor = YellowFill;
        ApplyBannerBorder(ws.Range($"A5:{lastCol}5"));

        // Row 7 — spacer between header row 6 and spill row 8
        ws.Row(7).Height = 16.5;
    }

    /// <summary>Applies Medium #808080 outside border on the merged banner range.</summary>
    private static void ApplyBannerBorder(IXLRange range)
    {
        range.Style.Border.TopBorder = PrintBorderStyle;
        range.Style.Border.BottomBorder = PrintBorderStyle;
        range.Style.Border.LeftBorder = PrintBorderStyle;
        range.Style.Border.RightBorder = PrintBorderStyle;
        range.Style.Border.TopBorderColor = PrintBorderColor;
        range.Style.Border.BottomBorderColor = PrintBorderColor;
        range.Style.Border.LeftBorderColor = PrintBorderColor;
        range.Style.Border.RightBorderColor = PrintBorderColor;
    }

    /// <summary>Writes the header row — bold, no fill, Thin #808080 border per cell, bottom-aligned,
    /// row height 32 to match the screenshot.</summary>
    private static void WritePrintSheetHeaders(IXLWorksheet ws, int headerRow, string[] headers)
    {
        for (int i = 0; i < headers.Length; i++)
            ws.Cell(headerRow, i + 1).Value = headers[i];
        var range = ws.Range(headerRow, 1, headerRow, headers.Length);
        range.Style.Font.Bold = true;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Bottom;
        range.Style.Border.TopBorder = PrintBorderStyle;
        range.Style.Border.BottomBorder = PrintBorderStyle;
        range.Style.Border.LeftBorder = PrintBorderStyle;
        range.Style.Border.RightBorder = PrintBorderStyle;
        range.Style.Border.TopBorderColor = PrintBorderColor;
        range.Style.Border.BottomBorderColor = PrintBorderColor;
        range.Style.Border.LeftBorderColor = PrintBorderColor;
        range.Style.Border.RightBorderColor = PrintBorderColor;
        ws.Row(headerRow).Height = 32;
    }

    /// <summary>Applies Medium #808080 borders on data/tariff/gap rows only, driven by the hidden
    /// InDataBlock flag column. Spans rows {spillRow}..1000 across visible columns 1..{lastCol}.
    /// The CF predicate matches when the flag column equals 1; footer and notes rows have blank
    /// flag values and remain unbordered.</summary>
    private static void ApplyDataBorderConditionalFormat(
        IXLWorksheet ws, int spillRow, int lastVisibleCol, string flagColLetter)
    {
        var range = ws.Range(spillRow, 1, 1000, lastVisibleCol);
        var cf = range.AddConditionalFormat()
            .WhenIsTrue($"${flagColLetter}{spillRow}=1");
        cf.Border.SetTopBorder(PrintBorderStyle).Border.SetTopBorderColor(PrintBorderColor);
        cf.Border.SetBottomBorder(PrintBorderStyle).Border.SetBottomBorderColor(PrintBorderColor);
        cf.Border.SetLeftBorder(PrintBorderStyle).Border.SetLeftBorderColor(PrintBorderColor);
        cf.Border.SetRightBorder(PrintBorderStyle).Border.SetRightBorderColor(PrintBorderColor);
    }

    /// <summary>Applies footer styling: bold right-aligned labels on the Qty column, Medium
    /// #808080 top border on Subtotal and Grand Total amount cells (Buy Ext + Sell Ext), and
    /// bold Grand Total amount cells. Spans rows {spillRow}..1000.</summary>
    private static void ApplyFooterStyling(
        IXLWorksheet ws, int spillRow, int qtyCol, int buyExtCol, int sellExtCol)
    {
        string qtyLetter = XLHelper.GetColumnLetterFromNumber(qtyCol);
        string[] labels =
        {
            "Fixture Package Sub-Total:",
            "Lutron Lighting Control Sub-Total:",
            "Estimated Freight:",
            "LIGHTING PACKAGE TOTAL:",
        };

        // Bold + right-align any Qty-column cell that equals one of the footer labels
        string labelPredicate = string.Join(",", labels.Select(l => $"${qtyLetter}{spillRow}=\"{l}\""));
        var qtyRange = ws.Range(spillRow, qtyCol, 1000, qtyCol);
        var qtyCf = qtyRange.AddConditionalFormat().WhenIsTrue($"OR({labelPredicate})");
        qtyCf.Font.SetBold();

        // Subtotal row — top border on Buy Ext and Sell Ext cells only (skip Sell Ea. between them)
        foreach (int col in new[] { buyExtCol, sellExtCol })
        {
            var subCf = ws.Range(spillRow, col, 1000, col).AddConditionalFormat()
                .WhenIsTrue($"${qtyLetter}{spillRow}=\"Fixture Package Sub-Total:\"");
            subCf.Border.SetTopBorder(PrintBorderStyle).Border.SetTopBorderColor(PrintBorderColor);
        }

        // Grand Total row — top border + bold on Buy Ext and Sell Ext cells only
        foreach (int col in new[] { buyExtCol, sellExtCol })
        {
            var grandCf = ws.Range(spillRow, col, 1000, col).AddConditionalFormat()
                .WhenIsTrue($"${qtyLetter}{spillRow}=\"LIGHTING PACKAGE TOTAL:\"");
            grandCf.Border.SetTopBorder(PrintBorderStyle).Border.SetTopBorderColor(PrintBorderColor);
            grandCf.Font.SetBold();
        }
    }

    #endregion

    #region Changes Sheet

    // Hidden marker column. Each AppendChanges run writes "|" to this column on the last
    // row of its batch so subsequent appends can re-draw divider lines after the banding
    // pass overwrites them. Same-date repeat runs only differ here, so this is also how
    // we distinguish them visually.
    private const int ChangesBatchMarkerCol = 6;

    private static void BuildChangesSheet(IXLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Changes");

        ws.Cell(1, 1).Value = "Date";
        ws.Cell(1, 2).Value = "Type";
        ws.Cell(1, 3).Value = "Change";
        ws.Cell(1, 4).Value = "Old Value";
        ws.Cell(1, 5).Value = "New Value";

        ApplyRawSheetStyling(ws, lastCol: 5, lastDataRow: 1);

        ws.Column(1).Width = 14;
        ws.Column(2).Width = 12;
        ws.Column(3).Width = 18;
        ws.Column(4).Width = 30;
        ws.Column(5).Width = 30;
        ws.Column(ChangesBatchMarkerCol).Hide();

        ApplyStandardPageSetup(ws);
    }

    #endregion

    #region Update Logic

    private static void UpdateWorksheetSheet(
        IXLWorkbook wb,
        List<CountsFixtureModel> fixtures,
        string countsSheetName,
        List<WorksheetRowData> existingRows,
        Dictionary<string, CountsFixtureModel>? prevData)
    {
        if (!wb.Worksheets.TryGetWorksheet("Worksheet", out var ws))
        {
            // No existing Worksheet — build fresh
            BuildWorksheetSheet(wb, fixtures, countsSheetName, null);
            return;
        }

        string csRef = $"'{countsSheetName}'";

        // When the user has selected a Reference Counts baseline on Dashboard!B11, col E
        // is written as a live SUMIFS formula against that historical Counts sheet (with
        // the literal-fallback baked into IFERROR). When B11 is blank, E stays a literal —
        // matching the legacy "compare against latest prior run" behavior. Toggling the
        // mode requires re-running TurboDocs.
        bool useReferenceCountsFormula = HasReferenceCountsBaseline(wb);

        // Build lookup of existing rows by (Type, Catalog)
        var existingByKey = new Dictionary<(string, string), WorksheetRowData>();
        foreach (var er in existingRows)
        {
            var key = (er.Type.ToUpperInvariant(), er.Catalog.ToUpperInvariant());
            existingByKey.TryAdd(key, er);
        }

        // Build set of new (Type, Catalog) pairs
        var newTypeMarks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newKeys = new HashSet<(string, string)>();
        var newRowEntries = new List<(string Type, string Mfr, string Catalog, int CatPosition)>();
        var fixtureByType = new Dictionary<string, CountsFixtureModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fixtures)
        {
            newTypeMarks.Add(f.TypeMark);
            fixtureByType.TryAdd(f.TypeMark, f);
            for (int c = 0; c < 6; c++)
            {
                string catNum = f.CatalogNumbers[c] ?? "";
                if (string.IsNullOrWhiteSpace(catNum)) continue;
                var key = (f.TypeMark.ToUpperInvariant(), catNum.ToUpperInvariant());
                newKeys.Add(key);
                newRowEntries.Add((f.TypeMark, f.Manufacturer, catNum, c));
            }
        }

        // Determine which existing types existed before (from prev data)
        var prevTypeMarks = prevData?.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Unprotect before editing
        ws.Unprotect();

        // Clear all existing styling (highlights from prior update)
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        if (lastRow > 1)
        {
            var dataRange = ws.Range(2, 1, lastRow, WsColQtyOverride);
            dataRange.Style.Fill.BackgroundColor = XLColor.NoColor;
            dataRange.Style.Font.Strikethrough = false;
        }

        // Step 1: Delete rows that were already marked as removed (red strikethrough)
        // We detect these by checking if the row's (Type, Catalog) is not in new data
        // AND was already not in prev data (meaning it was marked red last time)
        // NOTE: row 2 must never be deleted — the helper pipeline spill anchors live there
        // (AA2, AB2, …) and the print sheets reference them via ANCHORARRAY. Deleting row 2
        // causes ClosedXML to shift those cross-sheet references (AD2# → AD1#). Clear in
        // place instead.
        for (int r = lastRow; r >= 2; r--)
        {
            string rowType = ws.Cell(r, WsColType).GetString();
            string rowCat = ws.Cell(r, WsColCatalog).GetString();
            var key = (rowType.ToUpperInvariant(), rowCat.ToUpperInvariant());

            if (!newKeys.Contains(key) && ws.Cell(r, WsColType).Style.Font.Strikethrough)
            {
                if (r == 2)
                    ws.Row(r).Clear(XLClearOptions.Contents | XLClearOptions.NormalFormats);
                else
                    ws.Row(r).Delete();
            }
        }

        // Re-read existing rows after deletions
        existingRows = ReadExistingWorksheetRows(wb);
        existingByKey.Clear();
        foreach (var er in existingRows)
        {
            var key = (er.Type.ToUpperInvariant(), er.Catalog.ToUpperInvariant());
            existingByKey.TryAdd(key, er);
        }

        // Step 2: Clear existing data rows and rebuild.
        // Row 2 is cleared in place (see note in step 1); rows 3+ are safe to delete.
        lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        if (lastRow >= 3)
        {
            for (int r = lastRow; r >= 3; r--)
                ws.Row(r).Delete();
        }
        if (lastRow >= 2)
        {
            // Clear contents + formats + conditional formats on the visible columns A..helper-last.
            // CFs are included because rows 3+ are deleted (which removes their CFs), but row 2 is
            // cleared in place — without this, stale per-cell CFs (e.g. the Delta yellow-on-change
            // rule) accumulate across update passes and override the green new-type fill.
            // The helper-pipeline cells at row 2 will be overwritten by WriteHelperPipeline below.
            ws.Range(2, 1, 2, WsColHelperLast).Clear(
                XLClearOptions.Contents | XLClearOptions.NormalFormats | XLClearOptions.ConditionalFormats);
        }

        // Step 3: Write all rows sorted by Type Mark then catalog position
        newRowEntries.Sort((a, b) =>
        {
            int cmp = string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : a.CatPosition.CompareTo(b.CatPosition);
        });

        // Also include removed rows (in existing but not in new)
        var removedEntries = new List<(string Type, string Mfr, string Catalog)>();
        foreach (var er in existingRows)
        {
            var key = (er.Type.ToUpperInvariant(), er.Catalog.ToUpperInvariant());
            if (!newKeys.Contains(key))
                removedEntries.Add((er.Type, er.Mfr, er.Catalog));
        }

        // Count how many rows each catalog appears on — only mark canonical when siblings exist
        var catalogCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in newRowEntries)
            catalogCounts[entry.Catalog] = catalogCounts.GetValueOrDefault(entry.Catalog) + 1;

        // Pre-pass: determine canonical sheet row for each catalog (source of truth for
        // formula-chained duplicates). Prefer the first row in sort order whose existing
        // Unit Cost is a literal number. Unit Cost is the most reliable signal that a row
        // has real pricing data — Description is often empty or inconsistent.
        var canonicalSheetRowByCatalog = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < newRowEntries.Count; i++)
        {
            int sheetRow = 2 + i;
            var (type, _, catalog, _) = newRowEntries[i];
            var key = (type.ToUpperInvariant(), catalog.ToUpperInvariant());
            var existing = existingByKey.GetValueOrDefault(key);
            bool hasLiteralCost = existing != null && existing.UnitCost.HasValue && !existing.CostIsFormula;
            if (hasLiteralCost && !canonicalSheetRowByCatalog.ContainsKey(catalog))
                canonicalSheetRowByCatalog[catalog] = sheetRow;
        }
        // Fallback: for catalogs with no literal-cost row, use first occurrence
        for (int i = 0; i < newRowEntries.Count; i++)
        {
            int sheetRow = 2 + i;
            var (_, _, catalog, _) = newRowEntries[i];
            if (!canonicalSheetRowByCatalog.ContainsKey(catalog))
                canonicalSheetRowByCatalog[catalog] = sheetRow;
        }

        // Type-canonical sheet row: first emitted row of each Type in sort order (tariff source).
        var typeCanonicalSheetRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < newRowEntries.Count; i++)
        {
            int sheetRow = 2 + i;
            var (type, _, _, _) = newRowEntries[i];
            typeCanonicalSheetRow.TryAdd(type, sheetRow);
        }

        // Catalog → canonical Calc literal from the prior pass. Used to recompute PrevQty
        // when the Qty formula's cached value wasn't preserved (ClosedXML doesn't emit caches
        // on fresh FormulaA1 writes, so a user who never opens the file in Excel between
        // passes loses the cache on pass 3+).
        var prevCalcByCatalog = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var er in existingRows)
        {
            if (!er.CalcIsFormula && !string.IsNullOrWhiteSpace(er.Calc))
                prevCalcByCatalog.TryAdd(er.Catalog, er.Calc);
        }

        // Type → Tariff literal from the prior pass. Tariff lives only on each Type's
        // canonical row; if sort order elects a different catalog as canonical this pass,
        // the new-canonical row's existingByKey lookup returns null and the tariff drops.
        // Index by Type so it survives any re-canonicalization within the Type.
        var prevTariffByType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var er in existingRows)
        {
            if (er.Tariff.HasValue && !prevTariffByType.ContainsKey(er.Type))
                prevTariffByType[er.Type] = er.Tariff.Value;
        }

        // Type → per-Note literals from the prior pass. Notes live only on each Type's
        // canonical row; sort order may elect a different catalog as canonical on the new
        // pass, so we index by Type to survive re-canonicalization. Mirrors prevTariffByType.
        var prevNotesByType = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var er in existingRows)
        {
            if (prevNotesByType.ContainsKey(er.Type)) continue;
            // Only treat as the canonical snapshot if at least one note is a real literal
            // (skip rows whose notes were the "---" placeholder, normalized to empty by ReadNotes).
            if (er.Notes.Any(s => !string.IsNullOrEmpty(s)))
                prevNotesByType[er.Type] = er.Notes;
        }

        // Paint alt-row banding before the row-write loops so per-row green/yellow/red
        // highlights assigned during the loops override the gray fill on alt rows.
        // (Painting after the loops would overpaint the green new-type fill on cols A:F.)
        int plannedLastDataRow = 2 + newRowEntries.Count + removedEntries.Count - 1;
        ApplyAltRowFill(ws, plannedLastDataRow);

        int row = 2;

        // Write new/matched rows
        foreach (var (type, mfr, catalog, _) in newRowEntries)
        {
            var key = (type.ToUpperInvariant(), catalog.ToUpperInvariant());
            var existing = existingByKey.GetValueOrDefault(key);
            bool isNewType = !prevTypeMarks.Contains(type);
            bool isNewRow = existing == null;
            int canonicalRow = canonicalSheetRowByCatalog[catalog];

            ws.Cell(row, WsColType).Value = type;
            ws.Cell(row, WsColMfr).Value = TrimMfrForDisplay(mfr);
            ws.Cell(row, WsColCatalog).Value = catalog;
            ws.Cell(row, WsColTypeRepeat).Value = type;
            ws.Cell(row, WsColQty).FormulaA1 = BuildQtyFormula(row, csRef);
            ws.Cell(row, WsColDelta).FormulaA1 = $"IF(E{row}=\"\",\"\",D{row}-E{row})";
            ws.Cell(row, WsColCalc).GetDataValidation().List("\"Reel,Channel,End Cap,Clip\"", true);

            // Carry Mfr/Qty overrides forward on matched rows. EffQty (P) is always rewritten.
            if (existing != null)
            {
                if (!string.IsNullOrEmpty(existing.MfrOverride))
                    ws.Cell(row, WsColMfrOverride).Value = existing.MfrOverride;
                if (existing.QtyOverride.HasValue)
                    ws.Cell(row, WsColQtyOverride).Value = existing.QtyOverride.Value;
            }
            ws.Cell(row, WsColEffQty).FormulaA1 = $"IF(V{row}=\"\",D{row},V{row})";
            ws.Cell(row, WsColEffQty).Style.NumberFormat.Format = "0";

            // Prev Qty — prefer the previous Worksheet's cached Qty (reflects Calc adjustments),
            // else recompute from the preserved canonical Calc + prev fixture lengths (cache may
            // be missing on 3rd+ passes — ClosedXML doesn't emit caches for rewritten formulas),
            // else fall back to raw Count for types that weren't present before.
            // When useReferenceCountsFormula is set, write the live SUMIFS instead, with the
            // computed literal preserved as the IFERROR fallback (so a typo'd / cleared B11
            // still yields the legacy "vs. latest run" number).
            if (existing?.PrevQty.HasValue == true)
            {
                double prevQtyLiteral = existing.PrevQty.Value;
                if (useReferenceCountsFormula)
                    ws.Cell(row, WsColPrevQty).FormulaA1 = BuildReferenceCountsFormula(row, prevQtyLiteral);
                else
                    ws.Cell(row, WsColPrevQty).Value = prevQtyLiteral;
            }
            else if (prevData != null && prevData.TryGetValue(type, out var prevFixture))
            {
                prevCalcByCatalog.TryGetValue(catalog, out string? prevCalc);
                double prevQtyLiteral = ComputeQtyForCalc(prevCalc, prevFixture);
                if (useReferenceCountsFormula)
                    ws.Cell(row, WsColPrevQty).FormulaA1 = BuildReferenceCountsFormula(row, prevQtyLiteral);
                else
                    ws.Cell(row, WsColPrevQty).Value = prevQtyLiteral;
            }
            else if (useReferenceCountsFormula)
            {
                // No literal fallback available; emit the formula with 0 as the IFERROR result.
                ws.Cell(row, WsColPrevQty).FormulaA1 = BuildReferenceCountsFormula(row, 0);
            }

            int typeCanonical = typeCanonicalSheetRow[type];

            if (existing != null)
            {
                // Preserve Phase (Calc is handled via shared pricing logic below)
                if (existing.Phase.HasValue)
                    ws.Cell(row, WsColPhase).Value = existing.Phase.Value;

                // Catalog-canonical fields
                WritePricingCells(ws, row, canonicalRow,
                    existing.Description, existing.Calc, existing.UnitCost, existing.Markup, existing.Adder,
                    existing.DescIsFormula, existing.CalcIsFormula, existing.CostIsFormula,
                    isNewRow: false);

                // Type-canonical field (Tariff): preserve existing literal on canonical row.
                // If this row's key had no tariff (e.g., a different catalog was canonical last
                // pass), fall back to the Type-indexed snapshot so the tariff survives re-canonicalization.
                double? tariffForRow = existing.Tariff
                    ?? (prevTariffByType.TryGetValue(type, out double t) ? t : (double?)null);
                WriteTariffCell(ws, row, typeCanonical, tariffForRow, isNewRow: false);

                // Type-canonical fields (Notes N–S): preserve existing literals verbatim. On
                // re-canonicalization (different catalog elected), fall back to the Type-indexed
                // snapshot. Notes are never overwritten by Revit data on update — user-owned
                // after the type's first creation.
                string[] notesForRow = existing.Notes.Any(s => !string.IsNullOrEmpty(s))
                    ? existing.Notes
                    : (prevNotesByType.TryGetValue(type, out var pn) ? pn : new string[6]);
                for (int n = 0; n < 6; n++)
                    WriteNoteCell(ws, row, typeCanonical, n, notesForRow[n]);
            }
            else
            {
                // New row — no existing data
                WritePricingCells(ws, row, canonicalRow,
                    null, null, null, null, null, false, false, false, isNewRow: true);

                // If this brand-new catalog happens to land on the Type-canonical slot and the
                // Type itself existed before, carry the prior tariff forward. For truly new
                // types (isNewType), no prior tariff exists — stays blank.
                if (!isNewType && prevTariffByType.TryGetValue(type, out double prevT))
                    WriteTariffCell(ws, row, typeCanonical, prevT, isNewRow: false);
                else
                    WriteTariffCell(ws, row, typeCanonical, null, isNewRow: true);

                // Notes: three cases for a brand-new row landing on the canonical slot —
                // (a) existing Type with preserved notes → carry forward,
                // (b) brand-new Type → seed from Revit (only time Revit data lands on Notes),
                // (c) existing Type with no prior notes → leave blank.
                string[] notesForRow;
                if (!isNewType && prevNotesByType.TryGetValue(type, out var pn))
                    notesForRow = pn;
                else if (isNewType && fixtureByType.TryGetValue(type, out var f))
                    notesForRow = f.Notes;
                else
                    notesForRow = new string[6];
                for (int n = 0; n < 6; n++)
                    WriteNoteCell(ws, row, typeCanonical, n, notesForRow[n]);

                if (isNewType)
                {
                    // Brand-new type: Prev Qty = 0 so delta shows +qty; green across Revit-side cells.
                    if (useReferenceCountsFormula)
                        ws.Cell(row, WsColPrevQty).FormulaA1 = BuildReferenceCountsFormula(row, 0);
                    else
                        ws.Cell(row, WsColPrevQty).Value = 0;
                    for (int col = 1; col <= WsColDelta; col++)
                        ws.Cell(row, col).Style.Fill.BackgroundColor = GreenFill;
                }
                else
                {
                    // New catalog under existing type: yellow through Catalog only (Qty delta handles qty changes).
                    for (int col = 1; col <= WsColCatalog; col++)
                        ws.Cell(row, col).Style.Fill.BackgroundColor = YellowFill;
                }
            }

            // Yellow delta cell when qty changed (non-blank, non-zero). Skipped on new-type
            // rows so their green fill isn't overridden.
            if (!isNewType)
            {
                ws.Cell(row, WsColDelta).AddConditionalFormat()
                    .WhenIsTrue($"AND(ISNUMBER(F{row}),F{row}<>0)")
                    .Fill.SetBackgroundColor(YellowFill);
            }

            // Mark canonical only when the catalog has siblings on this sheet
            if (row == canonicalRow && catalogCounts.GetValueOrDefault(catalog) > 1)
                MarkCanonicalRow(ws, row, catalog);

            // Active flag: live row
            ws.Cell(row, WsColActive).Value = 1;

            row++;
        }

        // Write removed rows with red strikethrough
        foreach (var (type, mfr, catalog) in removedEntries)
        {
            var key = (type.ToUpperInvariant(), catalog.ToUpperInvariant());
            var existing = existingByKey.GetValueOrDefault(key);

            ws.Cell(row, WsColType).Value = type;
            ws.Cell(row, WsColMfr).Value = TrimMfrForDisplay(mfr);
            ws.Cell(row, WsColCatalog).Value = catalog;
            ws.Cell(row, WsColTypeRepeat).Value = type;

            if (existing != null)
            {
                ws.Cell(row, WsColCalc).Value = existing.Calc;
                // Phase intentionally left blank — removed rows must not appear in Phase sheet FILTER results
                ws.Cell(row, WsColDesc).Value = existing.Description ?? "";
                if (existing.UnitCost.HasValue) ws.Cell(row, WsColUnitCost).Value = existing.UnitCost.Value;
                if (existing.Markup.HasValue) ws.Cell(row, WsColMarkup).Value = existing.Markup.Value;
                if (existing.Tariff.HasValue) ws.Cell(row, WsColTariff).Value = existing.Tariff.Value;
                if (existing.Adder.HasValue) ws.Cell(row, WsColAdder).Value = existing.Adder.Value;
                for (int n = 0; n < 6; n++)
                {
                    if (!string.IsNullOrEmpty(existing.Notes[n]))
                        ws.Cell(row, WsColNote1 + n).Value = existing.Notes[n];
                }
                if (!string.IsNullOrEmpty(existing.MfrOverride))
                    ws.Cell(row, WsColMfrOverride).Value = existing.MfrOverride;
                if (existing.QtyOverride.HasValue)
                    ws.Cell(row, WsColQtyOverride).Value = existing.QtyOverride.Value;
            }
            ws.Cell(row, WsColEffQty).FormulaA1 = $"IF(V{row}=\"\",D{row},V{row})";
            ws.Cell(row, WsColEffQty).Style.NumberFormat.Format = "0";

            // Red fill + strikethrough — extends through Qty Override so the whole row reads as removed
            for (int col = 1; col <= WsColQtyOverride; col++)
            {
                ws.Cell(row, col).Style.Fill.BackgroundColor = RedFill;
                ws.Cell(row, col).Style.Font.Strikethrough = true;
            }

            // Active flag: removed — excluded from spill filters
            ws.Cell(row, WsColActive).Value = 0;

            row++;
        }

        int lastDataRow = row - 1;

        // Hide gridlines so only the explicit type-group dividers read as separators
        ws.ShowGridLines = false;

        ApplyQtyColumnFormatting(ws);
        ApplyPricingColumnFormats(ws);
        ApplyWorksheetHeaderStyling(ws);
        ApplyWorksheetTypography(ws);
        ApplyWorksheetRowHeights(ws);
        ApplyWorksheetBorders(ws, lastDataRow);
        // Alt-row banding is painted before the row-write loops above so the per-row
        // green/yellow/red highlights override it; not re-applied here.

        // Override + Notes column formatting — mirrors BuildWorksheetSheet so update passes
        // don't drop it. 16.64 char-width ≈ 190px in the target environment.
        for (int n = 0; n < 6; n++)
            ws.Column(WsColNote1 + n).Width = 16.64;
        ws.Column(WsColMfrOverride).Width = 16.64;
        ws.Column(WsColQtyOverride).Width = 16.64;
        ws.Column(WsColQtyOverride).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Column(WsColQtyOverride).Style.NumberFormat.Format = "0";

        if (lastDataRow >= 2)
        {
            ws.Range(2, WsColMfrOverride, lastDataRow, WsColMfrOverride)
                .AddConditionalFormat()
                .WhenIsTrue($"LEN(U2)>0")
                .Font.SetBold().Font.SetFontColor(XLColor.Red);
            ws.Range(2, WsColQtyOverride, lastDataRow, WsColQtyOverride)
                .AddConditionalFormat()
                .WhenIsTrue($"LEN(V2)>0")
                .Font.SetBold().Font.SetFontColor(XLColor.Red);
        }

        // Light gray divider at the last row of each Type group
        ApplyTypeGroupDividers(ws, 2, lastDataRow);

        // Helper pipeline (AA-BJ) — re-emit with updated lastDataRow bounds
        WriteHelperPipeline(ws, lastDataRow);

        // Hide helper columns Z..BJ (no-op on re-runs)
        for (int col = WsColActive; col <= WsColHelperLast; col++)
            ws.Column(col).Hide();

        // Re-apply protection. Per-Type canonical fields — Tariff % (K) and Notes (N–S) —
        // are only editable on each Type's canonical (first) row.
        var typeCanonicalRowsSet = new HashSet<int>(typeCanonicalSheetRow.Values);
        for (int r = 2; r < row; r++)
        {
            for (int col = WsColDesc; col <= WsColQtyOverride; col++)
                ws.Cell(r, col).Style.Protection.SetLocked(false);
            if (!typeCanonicalRowsSet.Contains(r))
            {
                ws.Cell(r, WsColTariff).Style.Protection.SetLocked(true);
                for (int n = 0; n < 6; n++)
                    ws.Cell(r, WsColNote1 + n).Style.Protection.SetLocked(true);
            }
        }
        ws.Protect().AllowElement(XLSheetProtectionElements.FormatColumns);
    }

    private static readonly XLColor AutoFilledFontColor = XLColor.FromHtml("#B0B0B0");

    private static string DependentFormula(string column, int canonicalRow) =>
        $"IF({column}{canonicalRow}=\"\",\"dependent\",{column}{canonicalRow})";

    /// <summary>
    /// Measures the widest "catalog ~ description" string across the Worksheet so Quote/Phase
    /// sheets can size their combined Catalog Number column. Summing the two AutoFit widths
    /// overshoots because each already includes per-column padding.
    /// </summary>
    private static double ComputeCombinedCatalogWidth(IXLWorksheet wsSheet)
    {
        int lastRow = wsSheet.LastRowUsed()?.RowNumber() ?? 1;
        int maxLen = "Catalog Number".Length;
        for (int r = 2; r <= lastRow; r++)
        {
            string catalog = wsSheet.Cell(r, WsColCatalog).GetString();
            var descCell = wsSheet.Cell(r, WsColDesc);
            string desc = descCell.HasFormula ? string.Empty : descCell.GetString();
            int len = catalog.Length + (string.IsNullOrWhiteSpace(desc) ? 0 : desc.Length + 3); // " ~ "
            if (len > maxLen) maxLen = len;

            // Include the 6 per-Type notes — they spill into this column as NOTE: rows on the
            // print sheets. Placeholder "---" mirror cells don't need to count.
            for (int n = 0; n < 6; n++)
            {
                string note = wsSheet.Cell(r, WsColNote1 + n).GetString();
                if (note == "---" || string.IsNullOrEmpty(note)) continue;
                if (note.Length > maxLen) maxLen = note.Length;
            }
        }
        // Excel column-width units are sized to the digit "0"; letters average wider, so
        // raw char count undercounts. Multiply by 1.2 + small pad to match proportional text.
        // Clamp at 67 — long notes wrap instead of dragging the column wide; row height grows.
        double width = Math.Ceiling(maxLen * 1.2) + 3;
        return Math.Min(width, 67);
    }

    /// <summary>
    /// Computes the Mfr print-column width from the already-trimmed values in Worksheet!B,
    /// mirroring the display transformations applied on the print sheets
    /// (<see cref="BuildMfrDisplayFormula"/>): UPPER and the Environmental Lights → LUMEN SPEC
    /// substitution. Using AdjustToContents on Worksheet!B is unreliable across update passes,
    /// so we measure from the source data the same way <see cref="ComputeCombinedCatalogWidth"/> does.
    /// </summary>
    private static double ComputeMfrDisplayWidth(IXLWorksheet wsSheet)
    {
        int lastRow = wsSheet.LastRowUsed()?.RowNumber() ?? 1;
        int maxLen = "Mfr".Length;
        for (int r = 2; r <= lastRow; r++)
        {
            string mfr = wsSheet.Cell(r, WsColMfr).GetString();
            if (string.IsNullOrWhiteSpace(mfr)) continue;
            string display = mfr.Equals("Environmental Lights", StringComparison.OrdinalIgnoreCase)
                ? "LUMEN SPEC"
                : mfr.ToUpperInvariant();
            if (display.Length > maxLen) maxLen = display.Length;
        }
        return Math.Ceiling(maxLen * 1.2) + 3;
    }

    private static void MarkCanonicalRow(IXLWorksheet ws, int row, string catalog)
    {
        ws.Cell(row, WsColCatalog).GetComment().AddText(
            $"Source row for catalog {catalog}. Edit here; other rows with this catalog auto-fill from this one.");
    }

    private static void StyleAutoFilledCell(IXLCell cell)
    {
        cell.Style.Font.Italic = true;
        cell.Style.Font.FontColor = AutoFilledFontColor;
    }

    private static void WritePricingCells(
        IXLWorksheet ws, int row, int canonicalRow,
        string? existingDesc, string? existingCalc, double? existingCost, double? existingMarkup, double? existingAdder,
        bool descIsFormula, bool calcIsFormula, bool costIsFormula,
        bool isNewRow)
    {
        bool isCanonical = row == canonicalRow;

        // Markup % and Adder have NO canonical — every row holds its own literal (user-entered
        // or drag-filled). Preserve whatever value was read. `ReadExistingWorksheetRows`
        // migrates legacy DependentFormula cells by capturing the cached computed value, so
        // `existing*` already represents the correct literal regardless of prior schema.
        if (!isNewRow && existingMarkup.HasValue)
            ws.Cell(row, WsColMarkup).Value = existingMarkup.Value;
        if (!isNewRow && existingAdder.HasValue)
            ws.Cell(row, WsColAdder).Value = existingAdder.Value;

        if (isCanonical)
        {
            // Canonical row: Desc / Calc / Unit Cost get literal values — preserved across updates.
            // New rows start blank; pricing team enters manually.
            if (!isNewRow)
            {
                if (existingDesc != null) ws.Cell(row, WsColDesc).Value = existingDesc;
                if (!string.IsNullOrEmpty(existingCalc)) ws.Cell(row, WsColCalc).Value = existingCalc;
                if (existingCost.HasValue) ws.Cell(row, WsColUnitCost).Value = existingCost.Value;
            }
            return;
        }

        // Non-canonical row (Desc / Calc / Unit Cost): preserve user-entered literal, else
        // propagate from canonical. Calc uses a plain =H{canonical} ref (no "dependent" wrap)
        // so the dropdown stays usable.
        if (!isNewRow && !descIsFormula && existingDesc != null)
        {
            ws.Cell(row, WsColDesc).Value = existingDesc;
        }
        else
        {
            ws.Cell(row, WsColDesc).FormulaA1 = DependentFormula("H", canonicalRow);
            StyleAutoFilledCell(ws.Cell(row, WsColDesc));
        }

        if (!isNewRow && !calcIsFormula && !string.IsNullOrEmpty(existingCalc))
        {
            ws.Cell(row, WsColCalc).Value = existingCalc;
        }
        else
        {
            ws.Cell(row, WsColCalc).FormulaA1 = $"IF(I{canonicalRow}=\"\",\"\",I{canonicalRow})";
            StyleAutoFilledCell(ws.Cell(row, WsColCalc));
        }

        if (!isNewRow && !costIsFormula && existingCost.HasValue)
        {
            ws.Cell(row, WsColUnitCost).Value = existingCost.Value;
        }
        else
        {
            ws.Cell(row, WsColUnitCost).FormulaA1 = DependentFormula("J", canonicalRow);
            StyleAutoFilledCell(ws.Cell(row, WsColUnitCost));
        }
    }

    /// <summary>
    /// Writes the Tariff cell. Tariff is a per-Type value — only the first row of each Type
    /// holds a literal; non-canonical rows mirror the canonical value via a gray-italic
    /// formula so users see the tariff on every row but can only edit the canonical cell.
    /// Formula returns "" (not 0) when canonical is blank so locked rows stay visually quiet
    /// until a tariff is entered. Helper pipeline still resolves per-row tariff % via XLOOKUP
    /// into the full K column (it reads cached values, so the mirror formula is purely cosmetic).
    /// </summary>
    private static void WriteTariffCell(
        IXLWorksheet ws, int row, int typeCanonicalRow,
        double? existingTariff, bool isNewRow)
    {
        if (row == typeCanonicalRow)
        {
            if (!isNewRow && existingTariff.HasValue)
                ws.Cell(row, WsColTariff).Value = existingTariff.Value;
        }
        else
        {
            ws.Cell(row, WsColTariff).FormulaA1 =
                $"IF(L{typeCanonicalRow}=\"\",\"\",L{typeCanonicalRow})";
            StyleAutoFilledCell(ws.Cell(row, WsColTariff));
        }
    }

    /// <summary>
    /// Writes a Schedule Notes cell (N–S). Like Tariff, notes are per-Type and only the first
    /// row of each Type holds the canonical literal. Subsequent rows display the literal string
    /// "---" in gray italic as a visual placeholder — the Quote/Phase pipeline reads the
    /// canonical row directly via XLOOKUP, so the mirror cell's content is never consumed
    /// downstream. "Seed from Revit" only applies when the canonical row is being created for
    /// the first time; preserved values are passed through verbatim on update.
    /// </summary>
    private static void WriteNoteCell(
        IXLWorksheet ws, int row, int typeCanonicalRow, int noteIndex,
        string? seedOrExistingValue)
    {
        int col = WsColNote1 + noteIndex;
        if (row == typeCanonicalRow)
        {
            if (!string.IsNullOrEmpty(seedOrExistingValue))
                ws.Cell(row, col).Value = seedOrExistingValue;
        }
        else
        {
            ws.Cell(row, col).Value = "---";
            StyleAutoFilledCell(ws.Cell(row, col));
        }
    }

    // Mirrors ReadNumericCell's null-on-empty semantics for string cells, so a blank
    // Description isn't round-tripped as a literal "" that overrides the dependent formula.
    private static string? ReadTextCell(IXLCell cell)
    {
        if (cell.HasFormula) return null;
        if (cell.IsEmpty()) return null;
        string s = cell.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static double? ReadNumericCell(IXLCell cell)
    {
        if (cell.HasFormula) return null;
        if (cell.IsEmpty()) return null;
        return cell.TryGetValue<double>(out double v) ? v : null;
    }

    /// <summary>
    /// Reads the 6 Schedule Notes cells for a row. The canonical row holds the authoritative
    /// literal; dependent rows hold the "---" placeholder which we normalize away to empty
    /// string so it's never round-tripped back as a literal note on re-canonicalization.
    /// </summary>
    private static string[] ReadNotes(IXLWorksheet ws, int row)
    {
        var notes = new string[6];
        for (int n = 0; n < 6; n++)
        {
            string s = ws.Cell(row, WsColNote1 + n).GetString();
            notes[n] = s == "---" ? string.Empty : s;
        }
        return notes;
    }

    private static double? ReadCachedDouble(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;
        try
        {
            var v = cell.CachedValue;
            return v.IsNumber ? v.GetNumber() : (double?)null;
        }
        catch
        {
            return null;
        }
    }

    private static void AppendChanges(
        IXLWorkbook wb,
        List<CountsFixtureModel> fixtures,
        Dictionary<string, CountsFixtureModel> prevData)
    {
        if (!wb.Worksheets.TryGetWorksheet("Changes", out var ws))
            return;

        string dateStr = DateTime.Now.ToString("yyyy.MM.dd");
        int startRow = (ws.LastRowUsed()?.RowNumber() ?? 1) + 1;
        int row = startRow;

        var newByType = fixtures.ToDictionary(f => f.TypeMark, StringComparer.OrdinalIgnoreCase);

        // Added types
        foreach (var f in fixtures)
        {
            if (!prevData.ContainsKey(f.TypeMark))
            {
                WriteChangeRow(ws, ref row, dateStr, f.TypeMark, "Added", "", "");
            }
        }

        // Removed types
        foreach (var kvp in prevData)
        {
            if (!newByType.ContainsKey(kvp.Key))
            {
                WriteChangeRow(ws, ref row, dateStr, kvp.Key, "Removed", "", "");
            }
        }

        // Changed values
        foreach (var f in fixtures)
        {
            if (!prevData.TryGetValue(f.TypeMark, out var prev)) continue;

            if (f.Count != prev.Count)
                WriteChangeRow(ws, ref row, dateStr, f.TypeMark, "Qty", prev.Count.ToString(), f.Count.ToString());

            if (f.Manufacturer != prev.Manufacturer)
                WriteChangeRow(ws, ref row, dateStr, f.TypeMark, "Mfr", prev.Manufacturer, f.Manufacturer);

            if (Math.Abs(f.LinearLength - prev.LinearLength) > 0.01)
                WriteChangeRow(ws, ref row, dateStr, f.TypeMark, "Linear Length",
                    prev.LinearLength.ToString("F2"), f.LinearLength.ToString("F2"));

            for (int c = 0; c < 6; c++)
            {
                string oldCat = prev.CatalogNumbers[c] ?? "";
                string newCat = f.CatalogNumbers[c] ?? "";
                if (oldCat != newCat)
                    WriteChangeRow(ws, ref row, dateStr, f.TypeMark, $"Catalog Number {c + 1}", oldCat, newCat);
            }
        }

        if (row > startRow)
        {
            // Mark the last row of this batch in the hidden marker column so the divider
            // can be re-drawn on every subsequent append (banding wipes interior borders).
            ws.Cell(row - 1, ChangesBatchMarkerCol).Value = "|";
            ws.Column(ChangesBatchMarkerCol).Hide();

            // Reapply banding/borders across the full data range so newly appended rows
            // pick up the same styling as the existing ones (parity is anchored to row 3
            // inside the helper, so prior bands stay aligned).
            ApplyRawSheetBandingAndBorders(ws, lastCol: 5, firstDataRow: 2, lastDataRow: row - 1);

            // Re-apply the thin #262626 batch separator (same style the Worksheet uses
            // between Type groups) on every marked row — including prior batches whose
            // dividers were just clobbered by the banding pass.
            for (int r = 2; r <= row - 1; r++)
            {
                if (ws.Cell(r, ChangesBatchMarkerCol).GetString() != "|") continue;
                var divider = ws.Range(r, 1, r, 5);
                divider.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                divider.Style.Border.BottomBorderColor = XLColor.FromHtml("#262626");
            }
        }
    }

    private static void WriteChangeRow(IXLWorksheet ws, ref int row, string date, string type, string change, string oldVal, string newVal)
    {
        ws.Cell(row, 1).Value = date;
        ws.Cell(row, 2).Value = type;
        ws.Cell(row, 3).Value = change;
        WriteValueCell(ws.Cell(row, 4), oldVal);
        WriteValueCell(ws.Cell(row, 5), newVal);
        row++;
    }

    // Writes numeric-looking strings as real numbers (no "Number stored as text" warning)
    // and non-numeric strings as text. Left-aligned so numbers and strings line up visually.
    private static void WriteValueCell(IXLCell cell, string value)
    {
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        if (double.TryParse(value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double num))
        {
            cell.Value = num;
            // Preserve trailing-zero formatting (e.g., "117.84" stays as 117.84, not 117.84000001).
            int dotIdx = value.IndexOf('.');
            int decimals = dotIdx >= 0 ? value.Length - dotIdx - 1 : 0;
            cell.Style.NumberFormat.Format = decimals > 0 ? "0." + new string('0', decimals) : "0";
        }
        else
        {
            cell.Value = value;
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Applies the standard Counts print setup: Portrait, fit-all-columns-on-one-page,
    /// horizontally centered, with the shared margin profile. Callers may add
    /// sheet-specific extras (print area, repeating rows, page breaks) afterward.
    /// </summary>
    /// <summary>
    /// Applies the shared "raw data tab" styling: Segoe UI 11, dark header strip with
    /// amber font, no wrap, frozen top row, gridlines off, plus alt-row banding and a
    /// thin grid + medium outside border on the data range. Caller is responsible for
    /// column widths (we want columns to fit on screen so users don't scroll right).
    /// Safe to call before data is written by passing lastDataRow == 1; banding/borders
    /// will be skipped. AppendChanges re-applies banding/borders on newly added rows.
    /// </summary>
    private static void ApplyRawSheetStyling(IXLWorksheet ws, int lastCol, int lastDataRow)
    {
        ws.Style.Font.FontName = "Segoe UI";
        ws.Style.Font.FontSize = 11;
        ws.Style.Alignment.WrapText = false;
        ws.ShowGridLines = false;

        var header = ws.Range(1, 1, 1, lastCol);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#262626");
        header.Style.Font.FontColor = XLColor.FromHtml("#FACC75");
        ws.Row(1).Height = 45;

        ApplyRawSheetBandingAndBorders(ws, lastCol, firstDataRow: 2, lastDataRow);
    }

    private static void ApplyRawSheetBandingAndBorders(IXLWorksheet ws, int lastCol, int firstDataRow, int lastDataRow)
    {
        if (lastDataRow < firstDataRow) return;

        var fill = XLColor.FromHtml("#F2F2F2");
        // Band every other row, anchored to row 3 so the parity is stable across appends.
        int firstBand = firstDataRow + ((firstDataRow % 2 == 1) ? 0 : 1);
        for (int r = firstBand; r <= lastDataRow; r += 2)
            ws.Range(r, 1, r, lastCol).Style.Fill.BackgroundColor = fill;

        var data = ws.Range(firstDataRow, 1, lastDataRow, lastCol);
        data.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        data.Style.Border.InsideBorderColor = XLColor.FromHtml("#D9D9D9");
        data.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        data.Style.Border.OutsideBorderColor = XLColor.Black;
    }

    private static void ApplyStandardPageSetup(IXLWorksheet ws)
    {
        var ps = ws.PageSetup;
        ps.PageOrientation = XLPageOrientation.Portrait;
        ps.FitToPages(1, 0);
        ps.CenterHorizontally = true;

        ps.Margins.Top = 1.15;
        ps.Margins.Header = 0.301875;
        ps.Margins.Left = 0.7;
        ps.Margins.Right = 0.7036111;
        ps.Margins.Bottom = 0.75;
        ps.Margins.Footer = 0.3;
    }

    private static string ResolveCountsSheetName(IXLWorkbook wb, string dateString)
    {
        string baseName = $"Counts {dateString}";
        if (!wb.Worksheets.TryGetWorksheet(baseName, out _))
            return baseName;

        for (int i = 2; i < 100; i++)
        {
            string name = $"{baseName} ({i})";
            if (!wb.Worksheets.TryGetWorksheet(name, out _))
                return name;
        }
        return $"{baseName} ({Guid.NewGuid().ToString()[..4]})";
    }

    private static IXLWorksheet? FindLatestCountsSheet(IXLWorkbook wb)
    {
        return wb.Worksheets
            .Where(ws => ws.Name.StartsWith("Counts ", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(ws => ws.Name)
            .FirstOrDefault();
    }

    private static IEnumerable<IXLWorksheet> EnumerateCountsSheets(IXLWorkbook wb)
    {
        return wb.Worksheets
            .Where(ws => ws.Name.StartsWith("Counts ", StringComparison.OrdinalIgnoreCase))
            .OrderBy(ws => ws.Name);
    }

    private static string BuildCatComboFormula(int row) =>
        $"A{row}&\"|\"&C{row}&D{row}&E{row}&F{row}&G{row}&H{row}";

    private static Dictionary<string, CountsFixtureModel> ReadCountsSheetData(IXLWorksheet ws)
    {
        var result = new Dictionary<string, CountsFixtureModel>(StringComparer.OrdinalIgnoreCase);
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

        for (int r = 2; r <= lastRow; r++)
        {
            string typeMark = ws.Cell(r, CsColType).GetString();
            if (string.IsNullOrWhiteSpace(typeMark)) continue;

            var model = new CountsFixtureModel
            {
                TypeMark = typeMark,
                Manufacturer = ws.Cell(r, CsColMfr).GetString(),
                CatalogNumbers = new string[6],
                Count = (int)ws.Cell(r, CsColCount).GetDouble(),
                LinearLength = ws.Cell(r, CsColLinear).GetDouble(),
                ReelLength = ws.Cell(r, CsColReel).GetDouble(),
                ChannelLength = ws.Cell(r, CsColChannel).GetDouble(),
            };

            for (int c = 0; c < 6; c++)
                model.CatalogNumbers[c] = ws.Cell(r, CsColCat1 + c).GetString();

            result[typeMark] = model;
        }

        return result;
    }

    private static string ReadRepDirectoryPathFromDashboard(IXLWorkbook wb)
    {
        if (!wb.Worksheets.TryGetWorksheet("Dashboard", out var ws))
            return string.Empty;
        return ws.Cell(DashRepDirCell).GetString();
    }

    private static List<WorksheetRowData> ReadExistingWorksheetRows(IXLWorkbook wb)
    {
        var result = new List<WorksheetRowData>();
        if (!wb.Worksheets.TryGetWorksheet("Worksheet", out var ws))
            return result;

        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (int r = 2; r <= lastRow; r++)
        {
            var cell = ws.Cell(r, WsColType);
            string type = cell.GetString();
            if (string.IsNullOrWhiteSpace(type)) continue;

            result.Add(new WorksheetRowData
            {
                Row = r,
                Type = type,
                Mfr = ws.Cell(r, WsColMfr).GetString(),
                Catalog = ws.Cell(r, WsColCatalog).GetString(),
                Calc = ws.Cell(r, WsColCalc).HasFormula ? string.Empty : ws.Cell(r, WsColCalc).GetString(),
                PrevQty = ReadCachedDouble(ws.Cell(r, WsColQty)),
                Phase = ReadNumericCell(ws.Cell(r, WsColPhase)),
                Description = ReadTextCell(ws.Cell(r, WsColDesc)),
                UnitCost = ReadNumericCell(ws.Cell(r, WsColUnitCost)),
                Markup = ReadNumericCell(ws.Cell(r, WsColMarkup)),
                Adder = ReadNumericCell(ws.Cell(r, WsColAdder)),
                Tariff = ReadNumericCell(ws.Cell(r, WsColTariff)),
                DescIsFormula = ws.Cell(r, WsColDesc).HasFormula,
                CalcIsFormula = ws.Cell(r, WsColCalc).HasFormula,
                CostIsFormula = ws.Cell(r, WsColUnitCost).HasFormula,
                IsStrikethrough = ws.Cell(r, WsColType).Style.Font.Strikethrough,
                MfrOverride = ws.Cell(r, WsColMfrOverride).GetString(),
                QtyOverride = ReadNumericCell(ws.Cell(r, WsColQtyOverride)),
                Notes = ReadNotes(ws, r),
            });
        }

        return result;
    }

    #endregion

    #region Internal Types

    internal class WorksheetRowData
    {
        public int Row { get; init; }
        public string Type { get; init; } = string.Empty;
        public string Mfr { get; init; } = string.Empty;
        public string Catalog { get; init; } = string.Empty;
        public string Calc { get; init; } = string.Empty;
        public double? PrevQty { get; init; }
        public double? Phase { get; init; }
        public string? Description { get; init; }
        public double? UnitCost { get; init; }
        public double? Markup { get; init; }
        public double? Tariff { get; init; }
        public double? Adder { get; init; }
        public bool DescIsFormula { get; init; }
        public bool CalcIsFormula { get; init; }
        public bool CostIsFormula { get; init; }
        public bool IsStrikethrough { get; init; }
        public string MfrOverride { get; init; } = string.Empty;
        public double? QtyOverride { get; init; }
        public string[] Notes { get; init; } = new string[6];
    }

    #endregion

    #region Dynamic Array Post-Processing

    /// <summary>
    /// Patches the saved .xlsx to enable dynamic array spilling for formula cells.
    /// ClosedXML doesn't write the XLDAPR cell metadata that Excel requires, so formulas
    /// containing FILTER/VSTACK get the implicit intersection operator (@) added on open.
    /// This method injects xl/metadata.xml with the dynamic array property definition and
    /// adds cm="1" to the formula cells in each listed sheet.
    /// For sheets with a minColumn, only formula cells at or beyond that column are patched.
    /// </summary>
    private static void PatchDynamicArrayMetadata(string filePath, List<(string sheetName, int? minColumn)> sheets)
    {
        XNamespace sml = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace orel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace xda = "http://schemas.microsoft.com/office/spreadsheetml/2017/dynamicarray";

        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Update);

        // 1. Add xl/metadata.xml if missing
        if (archive.GetEntry("xl/metadata.xml") == null)
        {
            var metadataXml = new XDocument(
                new XDeclaration("1.0", "UTF-8", "yes"),
                new XElement(sml + "metadata",
                    new XAttribute(XNamespace.Xmlns + "xda", xda),
                    new XElement(sml + "metadataTypes",
                        new XAttribute("count", "1"),
                        new XElement(sml + "metadataType",
                            new XAttribute("name", "XLDAPR"),
                            new XAttribute("minSupportedVersion", "120000"),
                            new XAttribute("copy", "1"),
                            new XAttribute("pasteAll", "1"),
                            new XAttribute("pasteValues", "1"),
                            new XAttribute("merge", "1"),
                            new XAttribute("splitFirst", "1"),
                            new XAttribute("rowColShift", "1"),
                            new XAttribute("clearFormats", "1"),
                            new XAttribute("clearComments", "1"),
                            new XAttribute("assign", "1"),
                            new XAttribute("coerce", "1"),
                            new XAttribute("cellMeta", "1"))),
                    new XElement(sml + "futureMetadata",
                        new XAttribute("name", "XLDAPR"),
                        new XAttribute("count", "1"),
                        new XElement(sml + "bk",
                            new XElement(sml + "extLst",
                                new XElement(sml + "ext",
                                    new XAttribute("uri", "{bdbb8cdc-fa1e-496e-a857-3c3f30c029c3}"),
                                    new XElement(xda + "dynamicArrayProperties",
                                        new XAttribute("fDynamic", "1"),
                                        new XAttribute("fCollapsed", "0")))))),
                    new XElement(sml + "cellMetadata",
                        new XAttribute("count", "1"),
                        new XElement(sml + "bk",
                            new XElement(sml + "rc",
                                new XAttribute("t", "1"),
                                new XAttribute("v", "0"))))));

            var entry = archive.CreateEntry("xl/metadata.xml");
            using var stream = entry.Open();
            metadataXml.Save(stream);
        }

        // 2. Register metadata.xml in [Content_Types].xml
        var contentTypesEntry = archive.GetEntry("[Content_Types].xml")!;
        XDocument contentTypes;
        using (var stream = contentTypesEntry.Open())
            contentTypes = XDocument.Load(stream);

        bool hasMetadataOverride = contentTypes.Root!.Elements(ct + "Override")
            .Any(e => e.Attribute("PartName")?.Value == "/xl/metadata.xml");
        if (!hasMetadataOverride)
        {
            contentTypes.Root.Add(new XElement(ct + "Override",
                new XAttribute("PartName", "/xl/metadata.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheetMetadata+xml")));

            contentTypesEntry.Delete();
            var newEntry = archive.CreateEntry("[Content_Types].xml");
            using var stream = newEntry.Open();
            contentTypes.Save(stream);
        }

        // 3. Add relationship in xl/_rels/workbook.xml.rels
        var wbRelsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels")!;
        XDocument wbRels;
        using (var stream = wbRelsEntry.Open())
            wbRels = XDocument.Load(stream);

        const string metadataRelType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/sheetMetadata";
        bool hasMetadataRel = wbRels.Root!.Elements(rel + "Relationship")
            .Any(e => e.Attribute("Type")?.Value == metadataRelType);
        if (!hasMetadataRel)
        {
            // Find highest existing rId number
            int maxId = wbRels.Root.Elements(rel + "Relationship")
                .Select(e => e.Attribute("Id")?.Value ?? "")
                .Where(id => id.StartsWith("rId"))
                .Select(id => int.TryParse(id[3..], out int n) ? n : 0)
                .DefaultIfEmpty(0).Max();

            wbRels.Root.Add(new XElement(rel + "Relationship",
                new XAttribute("Id", $"rId{maxId + 1}"),
                new XAttribute("Type", metadataRelType),
                new XAttribute("Target", "metadata.xml")));

            wbRelsEntry.Delete();
            var newEntry = archive.CreateEntry("xl/_rels/workbook.xml.rels");
            using var stream = newEntry.Open();
            wbRels.Save(stream);
        }

        // 4. Find phase sheet files and add cm="1" to formula cells
        var wbEntry = archive.GetEntry("xl/workbook.xml")!;
        XDocument workbook;
        using (var stream = wbEntry.Open())
            workbook = XDocument.Load(stream);

        // Map sheet names to rIds
        var sheetNameSet = sheets.Select(s => s.sheetName).ToHashSet();
        var sheetRIds = new Dictionary<string, string>();
        foreach (var sheet in workbook.Root!.Descendants(sml + "sheet"))
        {
            string name = sheet.Attribute("name")?.Value ?? "";
            string rId = sheet.Attribute(orel + "id")?.Value ?? "";
            if (sheetNameSet.Contains(name))
                sheetRIds[name] = rId;
        }

        // Map rIds to file targets and patch formula cells
        foreach (var (sheetName, minColumn) in sheets)
        {
            if (!sheetRIds.TryGetValue(sheetName, out string? rId)) continue;

            string? target = wbRels.Root.Elements(rel + "Relationship")
                .FirstOrDefault(e => e.Attribute("Id")?.Value == rId)
                ?.Attribute("Target")?.Value;
            if (target == null) continue;

            // Target may be absolute ("/xl/worksheets/...") or relative ("worksheets/...")
            string entryPath = target.StartsWith("/") ? target[1..] : $"xl/{target}";
            var sheetEntry = archive.GetEntry(entryPath);
            if (sheetEntry == null) continue;

            XDocument sheetDoc;
            using (var stream = sheetEntry.Open())
                sheetDoc = XDocument.Load(stream);

            foreach (var cell in sheetDoc.Descendants(sml + "c"))
            {
                var f = cell.Element(sml + "f");
                if (f == null) continue;

                string cellRef = cell.Attribute("r")?.Value ?? "";

                if (minColumn.HasValue)
                {
                    int col = ColumnLetterToNumber(cellRef.TakeWhile(char.IsLetter).ToArray());
                    if (col < minColumn.Value) continue;
                }

                if (cell.Attribute("cm") == null)
                    cell.SetAttributeValue("cm", "1");

                if (f.Attribute("t") == null)
                    f.SetAttributeValue("t", "array");
                if (f.Attribute("ref") == null)
                    f.SetAttributeValue("ref", cellRef);
                if (f.Attribute("aca") == null)
                    f.SetAttributeValue("aca", "1");
                if (f.Attribute("ca") == null)
                    f.SetAttributeValue("ca", "1");
            }

            sheetEntry.Delete();
            var newSheetEntry = archive.CreateEntry(entryPath);
            using var stream2 = newSheetEntry.Open();
            sheetDoc.Save(stream2);
        }
    }

    private static int ColumnLetterToNumber(char[] letters)
    {
        int col = 0;
        foreach (char c in letters)
            col = col * 26 + (char.ToUpper(c) - 'A' + 1);
        return col;
    }

    #endregion

    #region Header/Footer Image Embedding

    /// <summary>
    /// Post-processes the saved xlsx to embed a center-header and/or center-footer image
    /// on the listed sheets using the legacy VML HF mechanism (&amp;G placeholder).
    /// Image dimensions are read from PNG/JPEG headers; shape size in points = pixels * 72 / 96.
    /// No-op if both paths are empty or files don't exist.
    /// </summary>
    private static void EmbedHeaderFooterImages(string filePath, string headerPath, string footerPath, string[] targetSheets)
    {
        bool hasHeader = !string.IsNullOrWhiteSpace(headerPath) && File.Exists(headerPath);
        bool hasFooter = !string.IsNullOrWhiteSpace(footerPath) && File.Exists(footerPath);
        if (!hasHeader && !hasFooter) return;

        XNamespace sml = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace orel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Update);

        // Resolve sheet name → worksheet entry path via workbook.xml + workbook.xml.rels
        var sheetEntryByName = ResolveSheetEntryPaths(archive, sml, rel, orel);

        byte[]? headerBytes = hasHeader ? File.ReadAllBytes(headerPath) : null;
        byte[]? footerBytes = hasFooter ? File.ReadAllBytes(footerPath) : null;
        (double wPt, double hPt)? headerSize = hasHeader ? GetImagePointSize(headerBytes!, headerPath) : null;
        (double wPt, double hPt)? footerSize = hasFooter ? GetImagePointSize(footerBytes!, footerPath) : null;
        string headerExt = hasHeader ? Path.GetExtension(headerPath).TrimStart('.').ToLowerInvariant() : "";
        string footerExt = hasFooter ? Path.GetExtension(footerPath).TrimStart('.').ToLowerInvariant() : "";
        if (headerExt == "jpg") headerExt = "jpeg";
        if (footerExt == "jpg") footerExt = "jpeg";

        // Write shared image bytes into xl/media (single copy, reused across sheets)
        string headerMedia = hasHeader ? $"xl/media/turboHF_header.{headerExt}" : "";
        string footerMedia = hasFooter ? $"xl/media/turboHF_footer.{footerExt}" : "";
        if (hasHeader && archive.GetEntry(headerMedia) == null)
            WriteBinary(archive, headerMedia, headerBytes!);
        if (hasFooter && archive.GetEntry(footerMedia) == null)
            WriteBinary(archive, footerMedia, footerBytes!);

        // Ensure Default content types exist for image extensions and vml
        EnsureContentTypeDefaults(archive, ct, headerExt, footerExt);

        int vmlIndex = GetNextVmlIndex(archive);

        foreach (var sheetName in targetSheets)
        {
            if (!sheetEntryByName.TryGetValue(sheetName, out string? sheetEntryPath) || sheetEntryPath == null)
                continue;

            string vmlEntryPath = $"xl/drawings/vmlDrawing{vmlIndex}.vml";
            string sheetRelsPath = SheetRelsPath(sheetEntryPath);

            // Build VML drawing relationships (image references)
            var vmlRels = new XDocument(new XElement(rel + "Relationships"));
            string? headerRid = null;
            string? footerRid = null;
            int ridCounter = 1;
            if (hasHeader)
            {
                headerRid = $"rId{ridCounter++}";
                vmlRels.Root!.Add(new XElement(rel + "Relationship",
                    new XAttribute("Id", headerRid),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image"),
                    new XAttribute("Target", $"../media/turboHF_header.{headerExt}")));
            }
            if (hasFooter)
            {
                footerRid = $"rId{ridCounter++}";
                vmlRels.Root!.Add(new XElement(rel + "Relationship",
                    new XAttribute("Id", footerRid),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image"),
                    new XAttribute("Target", $"../media/turboHF_footer.{footerExt}")));
            }
            string vmlRelsPath = $"xl/drawings/_rels/vmlDrawing{vmlIndex}.vml.rels";
            WriteXml(archive, vmlRelsPath, vmlRels);

            // Build VML drawing document
            string vmlContent = BuildHeaderFooterVml(headerRid, footerRid, headerSize, footerSize);
            var vmlEntry = archive.CreateEntry(vmlEntryPath);
            using (var stream = vmlEntry.Open())
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                writer.Write(vmlContent);

            // Add relationship from worksheet → VML drawing
            string vmlRid = AddSheetVmlRelationship(archive, sheetRelsPath, vmlIndex, rel);

            // Patch worksheet XML: headerFooter + legacyDrawingHF
            PatchSheetForHeaderFooter(archive, sheetEntryPath, vmlRid, hasHeader, hasFooter, sml, orel);

            vmlIndex++;
        }
    }

    private static Dictionary<string, string> ResolveSheetEntryPaths(ZipArchive archive, XNamespace sml, XNamespace rel, XNamespace orel)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var wbEntry = archive.GetEntry("xl/workbook.xml");
        var wbRelsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (wbEntry == null || wbRelsEntry == null) return map;

        XDocument wb, wbRels;
        using (var s = wbEntry.Open()) wb = XDocument.Load(s);
        using (var s = wbRelsEntry.Open()) wbRels = XDocument.Load(s);

        var relTargetById = wbRels.Root!.Elements(rel + "Relationship")
            .ToDictionary(e => e.Attribute("Id")!.Value, e => e.Attribute("Target")!.Value);

        foreach (var sheet in wb.Root!.Element(sml + "sheets")!.Elements(sml + "sheet"))
        {
            string name = sheet.Attribute("name")!.Value;
            string rid = sheet.Attribute(orel + "id")!.Value;
            if (relTargetById.TryGetValue(rid, out string? target))
            {
                string entryPath = target.StartsWith("/") ? target.TrimStart('/') : $"xl/{target}";
                map[name] = entryPath;
            }
        }
        return map;
    }

    private static string SheetRelsPath(string sheetEntryPath)
    {
        int lastSlash = sheetEntryPath.LastIndexOf('/');
        string dir = sheetEntryPath.Substring(0, lastSlash);
        string file = sheetEntryPath.Substring(lastSlash + 1);
        return $"{dir}/_rels/{file}.rels";
    }

    private static int GetNextVmlIndex(ZipArchive archive)
    {
        int max = 0;
        foreach (var e in archive.Entries)
        {
            var name = e.FullName;
            if (name.StartsWith("xl/drawings/vmlDrawing", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".vml", StringComparison.OrdinalIgnoreCase))
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                if (int.TryParse(stem.Substring("vmlDrawing".Length), out int n) && n > max)
                    max = n;
            }
        }
        return max + 1;
    }

    private static void EnsureContentTypeDefaults(ZipArchive archive, XNamespace ct, string headerExt, string footerExt)
    {
        var entry = archive.GetEntry("[Content_Types].xml")!;
        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);

        bool changed = false;
        var existingDefaults = doc.Root!.Elements(ct + "Default")
            .Select(e => e.Attribute("Extension")?.Value.ToLowerInvariant())
            .Where(v => v != null).ToHashSet();

        void AddDefault(string ext, string type)
        {
            if (!existingDefaults.Contains(ext))
            {
                doc.Root.Add(new XElement(ct + "Default",
                    new XAttribute("Extension", ext),
                    new XAttribute("ContentType", type)));
                existingDefaults.Add(ext);
                changed = true;
            }
        }

        AddDefault("vml", "application/vnd.openxmlformats-officedocument.vmlDrawing");
        if (!string.IsNullOrEmpty(headerExt))
            AddDefault(headerExt, headerExt == "png" ? "image/png" : "image/jpeg");
        if (!string.IsNullOrEmpty(footerExt))
            AddDefault(footerExt, footerExt == "png" ? "image/png" : "image/jpeg");

        if (changed)
        {
            entry.Delete();
            var newEntry = archive.CreateEntry("[Content_Types].xml");
            using var s = newEntry.Open();
            doc.Save(s);
        }
    }

    private static string AddSheetVmlRelationship(ZipArchive archive, string sheetRelsPath, int vmlIndex, XNamespace rel)
    {
        var entry = archive.GetEntry(sheetRelsPath);
        XDocument doc;
        if (entry != null)
        {
            using var s = entry.Open();
            doc = XDocument.Load(s);
        }
        else
        {
            doc = new XDocument(new XElement(rel + "Relationships"));
        }

        int maxRid = 0;
        foreach (var r in doc.Root!.Elements(rel + "Relationship"))
        {
            string id = r.Attribute("Id")!.Value;
            if (id.StartsWith("rId") && int.TryParse(id.Substring(3), out int n) && n > maxRid)
                maxRid = n;
        }
        string newRid = $"rId{maxRid + 1}";

        doc.Root.Add(new XElement(rel + "Relationship",
            new XAttribute("Id", newRid),
            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/vmlDrawing"),
            new XAttribute("Target", $"../drawings/vmlDrawing{vmlIndex}.vml")));

        entry?.Delete();
        var newEntry = archive.CreateEntry(sheetRelsPath);
        using (var s = newEntry.Open()) doc.Save(s);
        return newRid;
    }

    private static void PatchSheetForHeaderFooter(ZipArchive archive, string sheetEntryPath, string vmlRid,
        bool hasHeader, bool hasFooter, XNamespace sml, XNamespace orel)
    {
        var entry = archive.GetEntry(sheetEntryPath)!;
        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);

        var ws = doc.Root!;

        // Remove any existing headerFooter + legacyDrawingHF we'd conflict with
        ws.Elements(sml + "headerFooter").Remove();
        ws.Elements(sml + "legacyDrawingHF").Remove();

        string headerText = hasHeader ? "&C&G" : "";
        string footerText = hasFooter ? "&C&G" : "";

        var headerFooter = new XElement(sml + "headerFooter",
            new XAttribute("differentOddEven", "0"),
            new XAttribute("differentFirst", "0"),
            new XAttribute("scaleWithDoc", "0"),
            new XAttribute("alignWithMargins", "0"),
            new XElement(sml + "oddHeader", headerText),
            new XElement(sml + "oddFooter", footerText));

        var legacyDrawingHF = new XElement(sml + "legacyDrawingHF",
            new XAttribute(orel + "id", vmlRid));

        // Insert in CT_Worksheet order: ... pageSetup, headerFooter, rowBreaks/colBreaks, ..., drawing, legacyDrawing, legacyDrawingHF, drawingHF, ...
        InsertAfter(ws, headerFooter, sml,
            new[] { "headerFooter", "pageSetup", "pageMargins", "printOptions", "sheetData" });
        InsertAfter(ws, legacyDrawingHF, sml,
            new[] { "legacyDrawing", "drawing", "picture", "headerFooter", "pageSetup", "pageMargins" });

        entry.Delete();
        var newEntry = archive.CreateEntry(sheetEntryPath);
        using (var s = newEntry.Open()) doc.Save(s);
    }

    // Insert `child` immediately after the first existing element in `precedingNames` order.
    // Falls back to appending if none found.
    private static void InsertAfter(XElement parent, XElement child, XNamespace sml, string[] precedingNames)
    {
        foreach (var name in precedingNames)
        {
            var anchor = parent.Element(sml + name);
            if (anchor != null)
            {
                anchor.AddAfterSelf(child);
                return;
            }
        }
        parent.Add(child);
    }

    private static string BuildHeaderFooterVml(string? headerRid, string? footerRid,
        (double wPt, double hPt)? headerSize, (double wPt, double hPt)? footerSize)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<xml xmlns:v=\"urn:schemas-microsoft-com:vml\" ");
        sb.Append("xmlns:o=\"urn:schemas-microsoft-com:office:office\" ");
        sb.Append("xmlns:x=\"urn:schemas-microsoft-com:office:excel\">");
        sb.Append("<o:shapelayout v:ext=\"edit\"><o:idmap v:ext=\"edit\" data=\"1\"/></o:shapelayout>");
        sb.Append("<v:shapetype id=\"_x0000_t75\" coordsize=\"21600,21600\" o:spt=\"75\" o:preferrelative=\"t\" ");
        sb.Append("path=\"m@4@5l@4@11@9@11@9@5xe\" filled=\"f\" stroked=\"f\">");
        sb.Append("<v:stroke joinstyle=\"miter\"/>");
        sb.Append("<v:formulas>");
        sb.Append("<v:f eqn=\"if lineDrawn pixelLineWidth 0\"/>");
        sb.Append("<v:f eqn=\"sum @0 1 0\"/>");
        sb.Append("<v:f eqn=\"sum 0 0 @1\"/>");
        sb.Append("<v:f eqn=\"prod @2 1 2\"/>");
        sb.Append("<v:f eqn=\"prod @3 21600 pixelWidth\"/>");
        sb.Append("<v:f eqn=\"prod @3 21600 pixelHeight\"/>");
        sb.Append("<v:f eqn=\"sum @0 0 1\"/>");
        sb.Append("<v:f eqn=\"prod @6 1 2\"/>");
        sb.Append("<v:f eqn=\"prod @7 21600 pixelWidth\"/>");
        sb.Append("<v:f eqn=\"sum @8 21600 0\"/>");
        sb.Append("<v:f eqn=\"prod @7 21600 pixelHeight\"/>");
        sb.Append("<v:f eqn=\"sum @10 21600 0\"/>");
        sb.Append("</v:formulas>");
        sb.Append("<v:path o:extrusionok=\"f\" gradientshapeok=\"t\" o:connecttype=\"rect\"/>");
        sb.Append("<o:lock v:ext=\"edit\" aspectratio=\"t\"/>");
        sb.Append("</v:shapetype>");

        int spid = 1025;
        if (headerRid != null && headerSize.HasValue)
        {
            var (w, h) = headerSize.Value;
            sb.Append($"<v:shape id=\"CH\" o:spid=\"_x0000_s{spid++}\" type=\"#_x0000_t75\" ");
            sb.Append($"style='position:absolute;margin-left:0;margin-top:0;width:{w.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}pt;height:{h.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}pt;z-index:1'>");
            sb.Append($"<v:imagedata o:relid=\"{headerRid}\" o:title=\"header\"/>");
            sb.Append("<o:lock v:ext=\"edit\" rotation=\"t\"/>");
            sb.Append("</v:shape>");
        }
        if (footerRid != null && footerSize.HasValue)
        {
            var (w, h) = footerSize.Value;
            sb.Append($"<v:shape id=\"CF\" o:spid=\"_x0000_s{spid}\" type=\"#_x0000_t75\" ");
            sb.Append($"style='position:absolute;margin-left:0;margin-top:0;width:{w.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}pt;height:{h.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}pt;z-index:2'>");
            sb.Append($"<v:imagedata o:relid=\"{footerRid}\" o:title=\"footer\"/>");
            sb.Append("<o:lock v:ext=\"edit\" rotation=\"t\"/>");
            sb.Append("</v:shape>");
        }
        sb.Append("</xml>");
        return sb.ToString();
    }

    private static void WriteBinary(ZipArchive archive, string entryPath, byte[] bytes)
    {
        var entry = archive.CreateEntry(entryPath);
        using var s = entry.Open();
        s.Write(bytes, 0, bytes.Length);
    }

    private static void WriteXml(ZipArchive archive, string entryPath, XDocument doc)
    {
        var existing = archive.GetEntry(entryPath);
        existing?.Delete();
        var entry = archive.CreateEntry(entryPath);
        using var s = entry.Open();
        doc.Save(s);
    }

    // Parse image header for pixel width/height. Convert to points assuming 300 DPI
    // (points = pixels * 72 / 300). Falls back to 200x50 pt on unrecognized formats.
    private static (double wPt, double hPt) GetImagePointSize(byte[] bytes, string path)
    {
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".png" && bytes.Length >= 24 &&
                bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                int w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                int h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                return (w * 72.0 / 300.0, h * 72.0 / 300.0);
            }
            if ((ext == ".jpg" || ext == ".jpeg") && bytes.Length > 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            {
                int i = 2;
                while (i < bytes.Length - 9)
                {
                    if (bytes[i] != 0xFF) { i++; continue; }
                    byte marker = bytes[i + 1];
                    // SOF markers: C0-CF except C4, C8, CC
                    if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    {
                        int h = (bytes[i + 5] << 8) | bytes[i + 6];
                        int w = (bytes[i + 7] << 8) | bytes[i + 8];
                        return (w * 72.0 / 300.0, h * 72.0 / 300.0);
                    }
                    int segLen = (bytes[i + 2] << 8) | bytes[i + 3];
                    i += 2 + segLen;
                }
            }
        }
        catch { }
        return (200.0, 50.0);
    }

    #endregion
}
