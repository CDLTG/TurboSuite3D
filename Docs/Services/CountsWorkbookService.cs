using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClosedXML.Excel;
using TurboSuite.Docs.Models;
using TurboSuite.Shared.Helpers;

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
    // Hidden helper column — Type|Cat1Cat2…Cat6 concatenation used by the Bid Compare sheet's
    // SUMIFS lookup against the historical Counts snapshot selected on Dashboard!B12.
    private const int CsColCatCombo = 19;   // S
    // Frozen unit prices captured at snapshot write time so Bid Compare can show price changes
    // since the bid. Sell Ea. = (UnitCost * (1 + Markup)) + Adder; Buy Ea. = UnitCost.
    // Frozen Dashboard meta on column V (hidden): V1 = Lutron (B7), V2 = Freight Sell (B9),
    // V3 = Sales Tax rate (B6, written for future Bid Compare consumption — not read yet).
    // V4 = PriceState ("UNPRICED" at birth if Worksheet had no pricing yet; flipped to
    // "PRICED" by ReconcileBaselinePricing on the first re-export after B12 references it).
    // Sheet-scoped named ranges LutronFrozen / FreightSellFrozen / SalesTaxRateFrozen point
    // at these cells.
    private const int CsColFrozenMeta = 22; // V
    private const int CsPriceStateRow = 4;
    // Per-slot frozen Sell Ea on cols W..AB (hidden): one cell per Cat slot 1..6, each holding
    // the catalog's own SellEa at lock time. Worksheet pricing is per-catalog (each catalog has
    // its own J/K/M with dependent formulas keeping repeats in sync), so the snapshot must
    // mirror that — a single Type-level SellEa loses non-primary catalogs' bid prices and
    // would falsely diff in Bid Compare whenever a Type's primary catalog changes.
    private const int CsColSellEa1 = 23;    // W
    // Per-Type tariff % captured at snapshot write time, broadcast to every row of the Type
    // (every row of Type X holds the same value). Mirrors the SellEa pattern — every snapshot
    // row is self-contained for Bid Compare's static B5 calculation, no canonical-row lookup
    // needed at read time. Source is Worksheet col L, which is canonical-per-Type (literal only
    // on the Type's first row; other rows mirror via formula).
    private const int CsColTariffPct = 29;  // AC

    // Highlight colors
    private static readonly XLColor GreenFill = XLColor.FromHtml("#C6EFCE");
    private static readonly XLColor RedFill = XLColor.FromHtml("#FFC7CE");
    private static readonly XLColor YellowFill = XLColor.FromHtml("#FFEB9C");

    // Flags catalog numbers that look like Environmental Lights parts (EL- prefix followed
    // by an alphanumeric). Used on Rep Lists to surface EL- parts riding under another
    // vendor's type — a side effect of Revit's one-mfr-per-type limit. See feedback memory.
    private static readonly Regex EnvironmentalLightsCatalogPattern =
        new(@"^EL-[A-Z0-9]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    #endregion

    /// <summary>
    /// Creates a new Counts workbook. Rep directory path comes from TurboDocs user settings
    /// (CountsViewModel passes it); seeds Dashboard!B3 and is used to build the Rep Lists sheet.
    /// Descriptions and pricing are no longer auto-filled — pricing team enters them manually.
    /// </summary>
    public static void GenerateNew(
        List<CountsFixtureModel> fixtures,
        string projectName,
        string projectNumber,
        string projectLocation,
        string outputPath,
        string repDirectoryPath,
        DateTime headerDate,
        string headerImagePath = "",
        string footerImagePath = "")
    {
        CatalogQtyValidator.ValidateOrThrow(fixtures);
        CatalogLengthTokenValidator.ValidateOrThrow(fixtures);

        using var wb = new XLWorkbook();

        string dateString = headerDate.ToString("yyyy.MM.dd");
        string countsSheetName = $"Counts {dateString}";

        var repDirectory = ReadRepDirectory(repDirectoryPath);

        BuildCoverSheet(wb, projectName, projectNumber, projectLocation, headerDate);
        BuildDashboardSheet(wb, projectName, repDirectoryPath);
        BuildWorksheetSheet(wb, fixtures, countsSheetName, null);
        BuildRepListsSheet(wb, fixtures, repDirectory);
        BuildQuoteSheet(wb);
        for (int p = 1; p <= 3; p++)
            BuildPhaseQuoteSheet(wb, p);
        // Bid Compare and Changes are created on-demand (first GenerateUpdate that has a
        // baseline snapshot or recorded fixture changes, respectively).
        BuildCountsSheet(wb, fixtures, countsSheetName);

        // Dashboard was built before the Counts sheet existed; refresh the "Compare to" dropdown
        // now that today's snapshot is in place so the pricing team can lock the bid against the
        // initial generation without waiting for an update.
        RefreshReferenceCountsDropdown(wb);

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
            new[] { "Quote", "Bid Compare", "Phase 1", "Phase 2", "Phase 3" });
    }

    /// <summary>
    /// Opens an existing workbook and updates it with fresh Revit data.
    /// </summary>
    public static void GenerateUpdate(
        List<CountsFixtureModel> fixtures,
        string existingPath,
        string repDirectoryPath,
        DateTime headerDate,
        string headerImagePath = "",
        string footerImagePath = "")
    {
        CatalogQtyValidator.ValidateOrThrow(fixtures);
        CatalogLengthTokenValidator.ValidateOrThrow(fixtures);

        string stage = "open-workbook";
        try
        {
            using var wb = new XLWorkbook(existingPath);

            stage = "resolve-counts-name";
            string dateString = headerDate.ToString("yyyy.MM.dd");
            string countsSheetName = ResolveCountsSheetName(wb, dateString);

            stage = "read-prev-counts";
            var prevCountsSheet = FindLatestCountsSheet(wb);
            var prevData = prevCountsSheet != null ? ReadCountsSheetData(prevCountsSheet) : null;

            stage = "ensure-dashboard";
            EnsureDashboardSheet(wb, repDirectoryPath);

            stage = "set-cover-date";
            if (wb.Worksheets.TryGetWorksheet("Cover", out var coverWs))
                coverWs.Cell(CoverDateCell).Value = headerDate.ToString("MMM dd, yyyy");

            stage = "read-rep-directory";
            string effectiveRepPath = ReadRepDirectoryPathFromDashboard(wb);
            if (string.IsNullOrWhiteSpace(effectiveRepPath))
                effectiveRepPath = repDirectoryPath;
            var repDirectory = ReadRepDirectory(effectiveRepPath);

            stage = "read-existing-worksheet-rows";
            var existingRows = ReadExistingWorksheetRows(wb);

            stage = "build-pricing-snapshot";
            var pricingForSnapshot = new Dictionary<(string Type, string Catalog), WorksheetRowData>();
            foreach (var er in existingRows)
            {
                var key = (er.Type, er.Catalog);
                if (!pricingForSnapshot.ContainsKey(key))
                    pricingForSnapshot.Add(key, er);
            }

            stage = "build-counts-sheet";
            BuildCountsSheet(wb, fixtures, countsSheetName);

            stage = "refresh-reference-dropdown";
            RefreshReferenceCountsDropdown(wb);

            stage = "update-worksheet";
            UpdateWorksheetSheet(wb, fixtures, countsSheetName, existingRows, prevData);

            stage = "build-rep-lists";
            BuildRepListsSheet(wb, fixtures, repDirectory);

            stage = "rebuild-contractor-sheets";
            RebuildContractorSheets(wb, fixtures, pricingForSnapshot);

            stage = "append-changes";
            if (prevData != null)
                AppendChanges(wb, fixtures, prevData, headerDate);

            stage = "save";
            wb.Save();

            stage = "patch-dynamic-array";
            var spillSheets = new List<(string sheetName, int? minColumn)>
            {
                ("Worksheet", WsColActive),
                ("Quote", null),
                ("Bid Compare", null),
                ("Phase 1", null),
                ("Phase 2", null),
                ("Phase 3", null),
            };
            PatchDynamicArrayMetadata(existingPath, spillSheets);

            stage = "embed-header-footer";
            EmbedHeaderFooterImages(existingPath, headerImagePath, footerImagePath,
                new[] { "Quote", "Bid Compare", "Phase 1", "Phase 2", "Phase 3" });
        }
        catch (Exception ex) when (!(ex is InvalidOperationException && ex.Message.StartsWith("[stage=")))
        {
            throw new InvalidOperationException(
                $"[stage={stage}] {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    #region Cover Sheet

    // Cover sheet anchor cells. Quote/Phase title-block formulas and the Bid Compare
    // project-name pull read these directly — keep in sync with WritePrintSheetTitle and
    // BuildBidCompareSheet.
    private const string CoverProjectNameCell = "A13";
    private const string CoverProjectLocationCell = "A14";
    private const string CoverDocTitleCell = "A16";
    private const string CoverDateCell = "A17";
    private const string CoverProjectNumberCell = "A19";
    private const string CoverForCell = "A21";
    private const string CoverPreparedByCell = "A22";
    private const string CoverEmailCell = "A23";
    private const string CoverPhoneCell = "A24";

    private static void BuildCoverSheet(IXLWorkbook wb, string projectName, string projectNumber, string projectLocation, DateTime headerDate)
    {
        var ws = wb.Worksheets.Add("Cover");
        ws.TabColor = XLColor.FromHtml("#FFF2F2F2");

        ws.Style.Font.FontName = "Segoe UI";
        ws.Style.Font.FontSize = 11;

        // Cols A:C width 28.6 — three equal columns merged across each title row to
        // center content over a wide-but-not-full-page footprint (matches TitleStyling.xlsx).
        for (int col = 1; col <= 3; col++)
            ws.Column(col).Width = 28.6;

        // Rows 1–12 left blank as top margin / branding area.

        // Row 13 — Project Name (Segoe UI 22 bold)
        var nameRange = ws.Range("A13:C13");
        nameRange.Merge();
        ws.Cell(CoverProjectNameCell).Value = projectName ?? string.Empty;
        StyleCenteredTitle(nameRange, fontSize: 22, bold: true);
        ws.Row(13).Height = 30;

        // Row 14 — City / State (Segoe UI 16 regular)
        var locRange = ws.Range("A14:C14");
        locRange.Merge();
        ws.Cell(CoverProjectLocationCell).Value = projectLocation ?? string.Empty;
        StyleCenteredTitle(locRange, fontSize: 16, bold: false);
        ws.Row(14).Height = 20;

        // Row 15 blank.

        // Row 16 — Document title (Segoe UI 16 bold)
        var docRange = ws.Range("A16:C16");
        docRange.Merge();
        ws.Cell(CoverDocTitleCell).Value = "Lighting Fixture Quotation";
        StyleCenteredTitle(docRange, fontSize: 16, bold: true);
        ws.Row(16).Height = 25;

        // Row 17 — Date (Segoe UI 12 bold). Seeded from TurboDocs settings header date.
        // Quote/Phase subtitles read this cell via formula, so manual edits flow through
        // until the next GenerateUpdate (which overwrites it with the current settings date).
        var dateRange = ws.Range("A17:C17");
        dateRange.Merge();
        ws.Cell(CoverDateCell).Value = headerDate.ToString("MMM dd, yyyy");
        StyleCenteredTitle(dateRange, fontSize: 12, bold: true);
        ws.Row(17).Height = 17.5;

        // Row 18 blank.

        // Row 19 — Project Number (Segoe UI 11 bold)
        var numRange = ws.Range("A19:C19");
        numRange.Merge();
        ws.Cell(CoverProjectNumberCell).Value = string.IsNullOrWhiteSpace(projectNumber)
            ? string.Empty
            : $"Project #{projectNumber}";
        StyleCenteredTitle(numRange, fontSize: 11, bold: true);

        // Row 20 blank.

        // Rows 21–24 — manually filled by pricing team (Segoe UI 11 regular).
        SeedManualCoverRow(ws, 21, "For: ");
        SeedManualCoverRow(ws, 22, "Prepared by: ");
        SeedManualCoverRow(ws, 23, "Email: ");
        SeedManualCoverRow(ws, 24, "Phone: ");

        ApplyStandardPageSetup(ws);
        ws.PageSetup.PrintAreas.Add("A1:C24");
    }

    private static void StyleCenteredTitle(IXLRange range, double fontSize, bool bold)
    {
        range.Style.Font.FontName = "Segoe UI";
        range.Style.Font.FontSize = fontSize;
        range.Style.Font.Bold = bold;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
    }

    private static void SeedManualCoverRow(IXLWorksheet ws, int row, string prefix)
    {
        var range = ws.Range(row, 1, row, 3);
        range.Merge();
        ws.Cell(row, 1).Value = prefix;
        StyleCenteredTitle(range, fontSize: 11, bold: false);
    }

    #endregion

    #region Dashboard Sheet

    // Dashboard cell anchors (consumed by helper pipeline via named ranges)
    private const string DashRepDirCell = "B3";
    private const string DashSalesTaxCell = "B6";
    private const string DashLutronCell = "B7";
    private const string DashFreightBuyCell = "B8";
    private const string DashFreightSellCell = "B9";
    private const string DashBidDateCell = "B12";
    private const string DashNotesFirstRow = "34";
    private const string DashNotesLastRow = "48";

    // Default quote-footer notes seeded into a freshly built Dashboard. Order is grouped
    // by theme: pricing & validity → billing → tax → fulfillment → contractor/exclusions.
    // Bold flag drives the print-sheet conditional format applied by ApplyNotesBoldConditionalFormat.
    private static readonly (string Text, bool Bold)[] DefaultQuoteNotes =
    {
        ("Pricing is firm for 30 days from the quote date.", false),
        ("CDL reserves the right to apply a 5–10% escalation to unit pricing in response to manufacturer increases.", false),
        ("This quote is void if the scope, quantities, or specifications change.", false),
        ("A 50% deposit is required to release the order; the balance is invoiced NET 30 upon shipment.", true),
        ("Stored material is invoiced upon receipt at CDL.", false),
        ("Freight charges are estimated and billed at actual cost.", false),
        ("Sales tax will be added for any manufacturer with tax nexus in the ship-to state.", true),
        ("The electrical contractor is responsible for determining whether materials in this proposal are subject to state sales tax.", false),
        ("Accurate lead times are confirmed only after the order is placed.", false),
        ("Standard manufacturer warranty applies unless otherwise noted.", false),
        ("The electrical contractor is responsible for verifying all quantities prior to bid; CDL is not liable for quantity errors.", false),
        ("No spare fixtures or lamps are included.", false),
    };

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
        ws.TabColor = XLColor.FromHtml("#FF7FC8F8");
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
        // Sales Tax is a percent applied to the fixture subtotal only. Quote/Phase sheets
        // omit the row when B6 is blank; an explicit 0% emits the row at $0.
        ws.Cell("A6").Value = "Sales Tax";
        ws.Cell("B6").Style.NumberFormat.Format = "0.00%";
        StyleEditableCell(ws.Cell("B6"));
        // Sits directly under the QUOTE ADJUSTMENTS bar — drop the top border so the gray
        // outline doesn't show against the dark #262626 fill above.
        ws.Cell("B6").Style.Border.TopBorder = XLBorderStyleValues.None;

        // Left blank by default — the Quote/Phase sheets omit the Lutron row when B7 is empty,
        // and blank cells coerce to 0 inside the Grand Total arithmetic.
        ws.Cell("A7").Value = "Lutron Lighting Control";
        ws.Cell("B7").Style.NumberFormat.Format = "$#,##0.00";
        StyleEditableCell(ws.Cell("B7"));

        ws.Cell("A8").Value = "Estimated Freight (Buy)";
        ws.Cell("B8").Style.NumberFormat.Format = "$#,##0.00";
        StyleEditableCell(ws.Cell("B8"));

        ws.Cell("A9").Value = "Estimated Freight (Sell)";
        ws.Cell("B9").Style.NumberFormat.Format = "$#,##0.00";
        StyleEditableCell(ws.Cell("B9"));

        // --- REFERENCE COUNTS ---
        // Live pointer to a historical Counts sheet. When set, Worksheet col E (and the
        // Quote Δ column it feeds) re-resolves against that snapshot via INDIRECT/SUMIFS.
        // When blank, behavior falls back to "compare against latest prior run."
        WriteSectionBar(ws, 11, "REFERENCE COUNTS");
        ws.Cell("A12").Value = "Compare to";
        ws.Cell("B12").Style.NumberFormat.Format = "yyyy-mm-dd";
        StyleEditableCell(ws.Cell("B12"));
        // Same dark-bar abutment fix as B6 — see comment above.
        ws.Cell("B12").Style.Border.TopBorder = XLBorderStyleValues.None;
        // Data-validation dropdown is wired up in RefreshReferenceCountsDropdown — that
        // helper also runs on every GenerateUpdate so the list stays in sync as new
        // Counts sheets accumulate.

        // Bold all column-A labels so they read as field captions against the input cells.
        foreach (string addr in new[] { "A3", "A6", "A7", "A8", "A9", "A12" })
            ws.Cell(addr).Style.Font.Bold = true;

        // --- INTERNAL NOTES ---
        WriteSectionBar(ws, 14, "INTERNAL NOTES");
        ws.Cell("A15").Value = "Date";
        ws.Cell("B15").Value = "Author";
        ws.Cell("C15").Value = "Status";
        ws.Cell("D15").Value = "Notes";
        StyleSubHeaderRow(ws.Range("A15:D15"));
        StyleInputBlock(ws.Range("A16:D30"), headerRow: ws.Range("A15:D15"));

        // --- QUOTE FOOTER NOTES ---
        WriteSectionBar(ws, 32, "QUOTE FOOTER NOTES");
        ws.Cell("A33").Value = "BOLD";
        ws.Cell("A33").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell("B33").Value = "Notes";
        StyleSubHeaderRow(ws.Range("A33:D33"));

        for (int i = 0; i < 15; i++)
        {
            int r = 34 + i;
            // Seed the first N rows from DefaultQuoteNotes; remaining rows stay blank for the user.
            bool boldDefault = i < DefaultQuoteNotes.Length && DefaultQuoteNotes[i].Bold;
            ws.Cell(r, 1).Value = boldDefault; // boolean literal — pass 3 upgrades to native checkbox
            ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            // Notes span B:D — merged so long notes aren't clipped by the narrow B column.
            ws.Range(r, 2, r, 4).Merge();
            ws.Cell(r, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            ws.Cell(r, 2).Style.Alignment.WrapText = true;
            if (i < DefaultQuoteNotes.Length)
                ws.Cell(r, 2).Value = DefaultQuoteNotes[i].Text;
        }
        StyleInputBlock(ws.Range("A34:D48"), headerRow: ws.Range("A33:D33"));

        // Constrain the BOLD column to TRUE/FALSE so the print-sheet CF
        // (which compares against the boolean TRUE) always matches what the user picks.
        var boldDv = ws.Range($"A{DashNotesFirstRow}:A{DashNotesLastRow}").CreateDataValidation();
        boldDv.List("\"TRUE,FALSE\"", inCellDropdown: true);
        boldDv.IgnoreBlanks = true;

        // Column widths
        ws.Column(1).Width = 24;
        ws.Column(2).Width = 18;
        ws.Column(3).Width = 40;
        ws.Column(4).Width = 70;

        // Named ranges
        wb.DefinedNames.Add("RepDirectoryPath", ws.Range("B3:B3"));
        wb.DefinedNames.Add("SalesTaxRate", ws.Range("B6:B6"));
        wb.DefinedNames.Add("LutronSubtotal", ws.Range("B7:B7"));
        wb.DefinedNames.Add("FreightBuy", ws.Range("B8:B8"));
        wb.DefinedNames.Add("FreightSell", ws.Range("B9:B9"));
        wb.DefinedNames.Add("BidDate", ws.Range("B12:B12"));
        wb.DefinedNames.Add("QuoteNotes", ws.Range($"B{DashNotesFirstRow}:B{DashNotesLastRow}"));
        wb.DefinedNames.Add("QuoteNotesBold", ws.Range($"A{DashNotesFirstRow}:A{DashNotesLastRow}"));

        // Protection: unlock editable cells, lock the rest
        foreach (string addr in new[] { "B3", "B6", "B7", "B8", "B9", "B12" })
            ws.Cell(addr).Style.Protection.SetLocked(false);
        ws.Range("A16:D30").Style.Protection.SetLocked(false);
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
            var old = wb.DefinedNames.FirstOrDefault(n =>
                string.Equals(n.Name, "PricingWorkbookPath", StringComparison.OrdinalIgnoreCase));
            old?.Delete();
        }

        string projectName = cover?.Cell(CoverProjectNameCell).GetString() ?? string.Empty;
        BuildDashboardSheet(wb, projectName, repDirectoryPath);
    }

    /// <summary>
    /// Repopulates the hidden helper range on Dashboard with the parsed dates of every
    /// Counts sheet currently in the workbook, then re-applies the data-validation list
    /// to B12. Called from BuildDashboardSheet and from GenerateUpdate so the dropdown
    /// stays in sync as new Counts sheets accumulate. Sheet protection is briefly
    /// suspended because the helper range and B12 are otherwise locked.
    /// </summary>
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

        // Re-apply data validation to B12. Use a closed (non-empty) range so Excel doesn't
        // show every empty Z row as a blank entry.
        var bidCell = ws.Cell(DashBidDateCell);
        bidCell.GetDataValidation().Clear();
        if (count > 0)
        {
            var listSource = $"=Dashboard!$Z$1:$Z${count}";
            var dv = bidCell.GetDataValidation();
            dv.List(listSource, true);
            dv.IgnoreBlanks = true;
        }
        bidCell.Style.Protection.SetLocked(false);

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
        // below the bar (B6, B12, …) don't bleed up into the dark fill. Row 1 uses
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

    // Special-case mfr collapses applied before generic suffix-stripping in both
    // NormalizeMfr (matching) and TrimMfrForDisplay (cosmetic). "Architectural" is kept
    // as a generic word, but the specific phrase "Extant Architectural" collapses to
    // "Extant" so the brand isn't split across two keys on Rep Lists.
    private static string ApplyMfrSpecialCases(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw ?? string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(
            raw,
            @"\bExtant\s+Architectural\b",
            "Extant",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

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

        raw = ApplyMfrSpecialCases(raw);

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

        raw = ApplyMfrSpecialCases(raw);

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
        ws.TabColor = XLColor.FromHtml("#FFB4A7D6");
        // Position between Worksheet and Quote
        if (wb.Worksheets.TryGetWorksheet("Worksheet", out var wsSheet))
            ws.Position = wsSheet.Position + 1;

        // Read project info from Cover sheet so each block carries the job identifiers
        // (pricing team copies a whole block into an email — name/location need to travel with it).
        string projectName = string.Empty;
        string projectLocation = string.Empty;
        if (wb.Worksheets.TryGetWorksheet("Cover", out var coverWs))
        {
            projectName = coverWs.Cell(CoverProjectNameCell).GetString().Trim();
            projectLocation = coverWs.Cell(CoverProjectLocationCell).GetString().Trim();
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
        // Length-token templates expand to one row per cut length so the Worksheet SUMIFS
        // can match resolved SKUs; the Mfr Override lookup also resolves per-length, so a
        // designer can route different cuts to different reps.
        var rows = new List<(string Type, string Mfr, string Catalog, int Qty)>();
        foreach (var f in fixtures)
        {
            for (int c = 0; c < 6; c++)
            {
                foreach (var (resolvedSku, qty) in CatalogLengthTokenResolver.ExpandSlot(f, c))
                {
                    string effMfr = mfrOverrideByKey.TryGetValue(
                        (f.TypeMark.ToUpperInvariant(), resolvedSku.ToUpperInvariant()), out var ovr)
                            ? ovr
                            : f.Manufacturer;
                    rows.Add((f.TypeMark, effMfr, resolvedSku, qty));
                }
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
                $"SUMIFS(Worksheet!BR:BR,Worksheet!A:A,A{row},Worksheet!C:C,C{row})";

            // Flag catalogs that exist under multiple rep blocks — typically the fallout of
            // an Mfr Override that moved a fixture's rep but left an older row behind until
            // the next Revit update regenerates the Rep List.
            // Two signals point to the same fix (set Mfr. Override + re-run from Revit), so
            // they share one yellow fill and one comment:
            //   1. Catalog appears under more than one rep block — usually fallout from an
            //      Mfr Override that moved a fixture but left an older row behind.
            //   2. Catalog has the EL- prefix but the row isn't in the Environmental Lights
            //      block — an EL part riding under another vendor's type because of Revit's
            //      one-mfr-per-type limit.
            bool crossRep = crossRepCatalogs.Contains(item.Catalog);
            bool elMismatch = !string.IsNullOrEmpty(item.Catalog)
                              && EnvironmentalLightsCatalogPattern.IsMatch(item.Catalog)
                              && repName.IndexOf("Environmental Lights", StringComparison.OrdinalIgnoreCase) < 0;
            if (crossRep || elMismatch)
            {
                ws.Cell(row, 3).Style.Fill.BackgroundColor = YellowFill;
                ws.Cell(row, 3).GetComment().AddText(
                    "Possible rep mismatch — set Mfr. Override on this Type and re-run from Revit.");
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

    private static void BuildCountsSheet(
        IXLWorkbook wb,
        List<CountsFixtureModel> fixtures,
        string sheetName)
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
        for (int s = 0; s < 6; s++)
            ws.Cell(1, CsColSellEa1 + s).Value = $"_SellEa{s + 1}";
        ws.Cell(1, CsColTariffPct).Value = "_TariffPct";

        // For per-slot SellEa freeze: resolve catalog Unit Cost via the canonical (master) row
        // for each catalog. Worksheet stores the literal J on the first occurrence of a catalog
        // and emits dependent formulas (`=IF(J<canon>="","dependent",J<canon>)`) on later
        // occurrences. We can't rely on cached formula values — on initial generation ClosedXML
        // hasn't evaluated formulas yet, and on update they may be stale — so we walk the
        // dependency graph here in C# instead. Markup (K) and Adder (M) are per-row literals,
        // so they're still read from the row itself.
        var wsRowByKey = new Dictionary<(string T, string C), int>();
        var catalogCanonicalRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Per-Type first row — used for tariff % lookup. Worksheet col L holds the literal only
        // on each Type's first row (others mirror via formula), so we resolve from the first row
        // seen for each Type. Don't skip strikethrough rows: a deleted Type's canonical L value
        // is still preserved in the cell and is what live XLOOKUP would also resolve.
        var typeFirstRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (wb.Worksheets.TryGetWorksheet("Worksheet", out var pricingWs))
        {
            int wsLast = pricingWs.LastRowUsed()?.RowNumber() ?? 1;
            for (int r2 = 2; r2 <= wsLast; r2++)
            {
                string t = pricingWs.Cell(r2, WsColType).GetString();
                string c2 = pricingWs.Cell(r2, WsColCatalog).GetString();
                if (string.IsNullOrWhiteSpace(t) || string.IsNullOrWhiteSpace(c2)) continue;
                var k = (t.ToUpperInvariant(), c2.ToUpperInvariant());
                if (!wsRowByKey.ContainsKey(k)) wsRowByKey.Add(k, r2);
                if (!catalogCanonicalRow.ContainsKey(c2)
                    && !pricingWs.Cell(r2, WsColUnitCost).HasFormula)
                    catalogCanonicalRow[c2] = r2;
                if (!typeFirstRow.ContainsKey(t)) typeFirstRow[t] = r2;
            }
        }

        // Data rows
        int row = 2;
        bool anyPriced = false;
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

            // Freeze pricing per slot. Each catalog has its own (J,K,M) on Worksheet — even
            // catalogs that share a name across Types are linked by dependent formula, not by
            // single-source. Storing per-slot SellEa keeps Bid Compare's diff accurate when a
            // Type's primary catalog later changes. Read direct from Worksheet cells so
            // dependent (formula-bearing) rows resolve to the correct cached value.
            if (pricingWs != null)
            {
                for (int c = 0; c < 6; c++)
                {
                    string cn = f.CatalogNumbers[c] ?? "";
                    if (string.IsNullOrWhiteSpace(cn)) continue;
                    var key = (f.TypeMark.ToUpperInvariant(), cn.ToUpperInvariant());
                    if (!wsRowByKey.TryGetValue(key, out int wsRow)) continue;

                    // Unit Cost: read from the catalog's canonical row (literal J), not the
                    // dependent row's formula cell which has no cached value at write time.
                    int costRow = catalogCanonicalRow.TryGetValue(cn, out int canon) ? canon : wsRow;
                    pricingWs.Cell(costRow, WsColUnitCost).TryGetValue(out double uc);
                    pricingWs.Cell(wsRow, WsColMarkup).TryGetValue(out double mk);
                    pricingWs.Cell(wsRow, WsColAdder).TryGetValue(out double ad);
                    double sellEa = (uc * (1 + mk)) + ad;
                    if (sellEa != 0)
                    {
                        ws.Cell(row, CsColSellEa1 + c).Value = sellEa;
                        ws.Cell(row, CsColSellEa1 + c).Style.NumberFormat.Format = "$#,##0.00";
                        anyPriced = true;
                    }
                }

                // Freeze the Type's tariff % from Worksheet col L (canonical row). Broadcast to
                // every snapshot row of this Type so Bid Compare's static B5 can read tariff
                // per-row without re-running a canonical lookup at compare time.
                double tariff = 0;
                if (typeFirstRow.TryGetValue(f.TypeMark, out int tRow))
                    pricingWs.Cell(tRow, WsColTariff).TryGetValue(out tariff);
                ws.Cell(row, CsColTariffPct).Value = tariff;
                ws.Cell(row, CsColTariffPct).Style.NumberFormat.Format = "0.00%";
            }

            row++;
        }
        int lastDataRow = row - 1;
        ws.Column(CsColCatCombo).Hide();
        for (int s = 0; s < 6; s++)
            ws.Column(CsColSellEa1 + s).Hide();
        ws.Column(CsColTariffPct).Hide();

        // Frozen Dashboard meta on column V (hidden). Sheet-scoped names so Bid Compare can
        // resolve the snapshot's bid-time Lutron / Freight Sell / Sales Tax rate from any sheet.
        if (wb.Worksheets.TryGetWorksheet("Dashboard", out var dashWs))
        {
            var lutronCell = dashWs.Cell(DashLutronCell);
            var freightCell = dashWs.Cell(DashFreightSellCell);
            var taxCell = dashWs.Cell(DashSalesTaxCell);
            if (!lutronCell.IsEmpty() && lutronCell.TryGetValue(out double lutronVal))
            {
                ws.Cell(1, CsColFrozenMeta).Value = lutronVal;
                ws.Cell(1, CsColFrozenMeta).Style.NumberFormat.Format = "$#,##0.00";
            }
            if (!freightCell.IsEmpty() && freightCell.TryGetValue(out double freightVal))
            {
                ws.Cell(2, CsColFrozenMeta).Value = freightVal;
                ws.Cell(2, CsColFrozenMeta).Style.NumberFormat.Format = "$#,##0.00";
            }
            if (!taxCell.IsEmpty() && taxCell.TryGetValue(out double taxVal))
            {
                ws.Cell(3, CsColFrozenMeta).Value = taxVal;
                ws.Cell(3, CsColFrozenMeta).Style.NumberFormat.Format = "0.00%";
            }
        }
        ws.DefinedNames.Add("LutronFrozen", ws.Range(1, CsColFrozenMeta, 1, CsColFrozenMeta));
        ws.DefinedNames.Add("FreightSellFrozen", ws.Range(2, CsColFrozenMeta, 2, CsColFrozenMeta));
        ws.DefinedNames.Add("SalesTaxRateFrozen", ws.Range(3, CsColFrozenMeta, 3, CsColFrozenMeta));
        // V4 = PriceState. "UNPRICED" means this snapshot was born before Worksheet pricing
        // existed; ReconcileBaselinePricing will refresh col T / V1 / V2 / V3 from current
        // Worksheet/Dashboard the first time B12 references it, then flip the flag to "PRICED".
        ws.Cell(CsPriceStateRow, CsColFrozenMeta).Value = anyPriced ? "PRICED" : "UNPRICED";
        ws.Column(CsColFrozenMeta).Hide();

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
        ws.TabColor = XLColor.FromHtml("#FFFACC75");
        ws.SheetView.FreezeRows(1);

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

        // Pre-pass: count occurrences keyed by *resolved* SKU. Length-token templates explode
        // into one row per unique cut length — counting templates would mis-classify those
        // rows as singletons and skip the canonical-row bold treatment.
        var catalogCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fixtures)
        {
            for (int c = 0; c < 6; c++)
            {
                // Count one increment per emitted row (not per fixture-instance qty) — the
                // canonical-row comment fires when a catalog appears on multiple rows.
                foreach (var (sku, _) in CatalogLengthTokenResolver.ExpandSlot(f, c))
                    catalogCounts[sku] = catalogCounts.GetValueOrDefault(sku) + 1;
            }
        }

        int row = 2;
        foreach (var f in fixtures)
        {
            for (int c = 0; c < 6; c++)
            {
                string template = f.CatalogNumbers[c] ?? "";
                if (string.IsNullOrWhiteSpace(template)) continue;

                // Per-slot context shared across the expansion loop. QtyX is parsed once;
                // ValidateOrThrow guarantees Parse succeeds, and rules out the
                // token + non-blank QtyX combination, so isExpanded short-circuits QtyX.
                var qtyRule = CatalogQtyParser.Parse(f.CatalogQtys[c]);
                bool isExpanded = CatalogLengthTokenResolver.HasToken(template);

                // ExpandSlot yields one (sku, qty) pair for plain templates, or one pair
                // per unique cut length for {L:...} tokens. catalogFirstRow is keyed by the
                // resolved SKU so each cut length owns its own pricing canonical row.
                foreach (var (resolvedSku, expandedQty) in CatalogLengthTokenResolver.ExpandSlot(f, c))
                {
                    ws.Cell(row, WsColType).Value = f.TypeMark;
                    ws.Cell(row, WsColMfr).Value = TrimMfrForDisplay(f.Manufacturer);
                    ws.Cell(row, WsColCatalog).Value = resolvedSku;
                    ws.Cell(row, WsColTypeRepeat).Value = f.TypeMark;

                    // Qty precedence: length-token literal > QtyX rule literal > Calc-driven formula.
                    if (isExpanded)
                        ws.Cell(row, WsColQty).Value = expandedQty;
                    else if (qtyRule.Mode == CatalogQtyMode.Default)
                        ws.Cell(row, WsColQty).FormulaA1 = BuildQtyFormula(row, csRef);
                    else
                        ws.Cell(row, WsColQty).Value = qtyRule.Evaluate(f.Count);

                    // Prev Qty — blank on first export. Delta picks up once it's populated.
                    ws.Cell(row, WsColDelta).FormulaA1 = $"IF(E{row}=\"\",\"\",D{row}-E{row})";
                    ws.Cell(row, WsColCalc).GetDataValidation().List("\"Reel,Channel,End Cap,Clip\"", true);

                    // Catalog-canonical fields (Description, Calc, Unit Cost): subsequent
                    // occurrences reference the first. Markup % and Adder have NO canonical —
                    // users drag-fill.
                    if (catalogFirstRow.TryGetValue(resolvedSku, out int firstRow))
                    {
                        // Desc / Unit Cost: empty canonical → "dependent" placeholder so the
                        // link is visible. Calc: plain ref (no wrap) — dropdown stays usable.
                        ws.Cell(row, WsColDesc).FormulaA1 = DependentFormula("H", firstRow);
                        ws.Cell(row, WsColCalc).FormulaA1 = $"IF(I{firstRow}=\"\",\"\",I{firstRow})";
                        ws.Cell(row, WsColUnitCost).FormulaA1 = DependentFormula("J", firstRow);
                        StyleAutoFilledCell(ws.Cell(row, WsColDesc));
                        StyleAutoFilledCell(ws.Cell(row, WsColCalc));
                        StyleAutoFilledCell(ws.Cell(row, WsColUnitCost));
                    }
                    else
                    {
                        catalogFirstRow[resolvedSku] = row;
                        // Description + Unit Cost start blank — pricing team enters manually.
                        // Mark canonical only when this SKU has siblings (single-row SKUs
                        // aren't visually distinguished, so the comment would be noise).
                        if (catalogCounts.GetValueOrDefault(resolvedSku) > 1)
                            MarkCanonicalRow(ws, row, resolvedSku);
                    }

                    // Tariff % (K) is per-Type canonical: only the first row of each Type
                    // holds a literal; subsequent rows mirror it via a gray-italic formula
                    // (WriteTariffCell) so users see the tariff on every row while the
                    // canonical cell remains the only editable one. Helper pipeline resolves
                    // the Type's tariff % via XLOOKUP against the full K column (first match
                    // = canonical).
                    //
                    // Schedule Notes (N–S) follow the same per-Type pattern: canonical row
                    // seeds from Revit on first creation; subsequent rows show "---" in
                    // gray italic.
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

                    // EffQty (BQ) = QtyOverride (U) if present, else Revit Qty (D). Single
                    // source of truth for effective qty — Rep Lists SUMIFS and the helper
                    // pipeline read this column instead of duplicating the override fallback.
                    ws.Cell(row, WsColEffQty).FormulaA1 = $"IF(V{row}=\"\",D{row},V{row})";
                    ws.Cell(row, WsColEffQty).Style.NumberFormat.Format = "0";

                    // Active flag: initial export has no removed rows — always 1.
                    ws.Cell(row, WsColActive).Value = 1;

                    row++;
                }
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
        // Optional rows (Sales Tax, Lutron) are omitted when the Dashboard cell is blank.
        // 2×2 matrix over (tax?, lutron?) — branching once per spill cell keeps the formula
        // shape Excel-compatible (FILTER over VSTACK trips the load-time parser).
        // Two leading blank rows buffer between the last data row and the totals block.
        string taxLabel = "\"Sales Tax (\"&TEXT(SalesTaxRate,\"0.00%\")&\"):\"";
        string sellTax = $"({sellSubtotal})*SalesTaxRate";
        string buyTax  = $"({buySubtotal})*SalesTaxRate";

        // 2×2×2 matrix over (tax?, lutron?, freight?) — freight row appears when either
        // FreightBuy or FreightSell has a value. N(FreightSell)/N(FreightBuy) coerce blank
        // cells to 0 for the grand total when only one side is populated.
        // VSTACK args: blank, blank, Subtotal, [Tax?], [Lutron?], [Freight?], Grand Total.
        // Sell/Buy stacks reference _xlpm.sub (bound via LET below) instead of inlining the
        // multi-hundred-char subtotal expression — keeps each footer formula well under
        // Excel's 8192-char per-formula ceiling across the 8 branches.
        string LabelStack(bool tax, bool lutron, bool freight)
        {
            var parts = new List<string> { "\"\"", "\"\"", "\"Fixture Package Sub-Total:\"" };
            if (tax)     parts.Add(taxLabel);
            if (lutron)  parts.Add("\"Lutron Lighting Control Sub-Total:\"");
            if (freight) parts.Add("\"Estimated Freight:\"");
            parts.Add("\"LIGHTING PACKAGE TOTAL:\"");
            return $"_xlfn.VSTACK({string.Join(",", parts)})";
        }
        string ValueStack(bool tax, bool lutron, bool freight, string freightCell, bool isSell)
        {
            const string sub = "_xlpm.sub";
            string taxExpr = $"{sub}*SalesTaxRate";
            var parts = new List<string> { "\"\"", "\"\"", sub };
            string total = sub;
            if (tax)     { parts.Add(taxExpr);          total += $"+({taxExpr})"; }
            if (lutron)  { parts.Add("LutronSubtotal"); total += "+LutronSubtotal"; }
            if (freight) { parts.Add(freightCell);      total += $"+N({freightCell})"; }
            parts.Add(total);
            return $"_xlfn.VSTACK({string.Join(",", parts)})";
        }
        string SellStack(bool tax, bool lutron, bool freight) => ValueStack(tax, lutron, freight, "FreightSell", true);
        string BuyStack(bool tax, bool lutron, bool freight)  => ValueStack(tax, lutron, freight, "FreightBuy",  false);

        // Freight predicate: row appears unless BOTH cells are blank.
        const string freightBlank = "AND(FreightBuy=\"\",FreightSell=\"\")";
        string Branch8(Func<bool, bool, bool, string> stack)
        {
            string Pair(bool tax, bool lutron) =>
                $"IF({freightBlank},{stack(tax, lutron, false)},{stack(tax, lutron, true)})";
            return "IF(SalesTaxRate=\"\","
                + $"IF(LutronSubtotal=\"\",{Pair(false,false)},{Pair(false,true)}),"
                + $"IF(LutronSubtotal=\"\",{Pair(true,false)},{Pair(true,true)}))";
        }

        string labelFooter = Branch8(LabelStack);
        string sellValueFooter = $"_xlfn.LET(_xlpm.sub,{sellSubtotal},{Branch8(SellStack)})";
        string buyValueFooter  = $"_xlfn.LET(_xlpm.sub,{buySubtotal},{Branch8(BuyStack)})";

        // Type-column padding before the spilled quote-footer notes. Must grow row-for-row with
        // the footer block so notes always sit a fixed gap below the Grand Total. Footer occupies
        // 4 rows (no opt) up to 7 (all opts). Pad = footer + 1-row visual gap.
        string TypePad(bool tax, bool lutron, bool freight)
        {
            int n = 5 + (tax ? 1 : 0) + (lutron ? 1 : 0) + (freight ? 1 : 0);
            return $"_xlfn.VSTACK({string.Join(",", Enumerable.Repeat("\"\"", n))})";
        }
        string typePadFooter = Branch8(TypePad);

        int i = 0;
        // Type — blank tariff row, blank note rows, dynamic pad sized to the footer block, then
        // quote footer notes appended at the bottom.
        ws.Cell($"{cols[i++]}2").FormulaA1 =
            $"_xlfn.VSTACK(IFERROR({Gap(Col("A"), "\"\"", NoteBlank())},\"\"),{typePadFooter},{notesSpill})";
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
        // Append a single 1 row — corresponds to the buffer row VSTACKed in front of the
        // footer block so the print-sheet border CF ($flagCol=1) paints that row too.
        ws.Cell($"{cols[i++]}2").FormulaA1 = $"_xlfn.VSTACK(IFERROR({flagLambda},\"\"),1)";
    }


    private static IXLWorksheet? ResolveBaselineSheet(IXLWorkbook wb)
    {
        var bidDate = ReadBidDate(wb);
        if (bidDate == null) return null;
        return wb.Worksheets.TryGetWorksheet($"Counts {bidDate:yyyy.MM.dd}", out var s) ? s : null;
    }

    /// <summary>
    /// First-time bid lock: when Dashboard B12 references a snapshot that was born before
    /// Worksheet pricing existed (V4 = "UNPRICED"), refresh its frozen Sell Ea. (col T) and
    /// Lutron / Freight / Sales Tax (V1-V3) from current Worksheet/Dashboard, then flip
    /// V4 to "PRICED" so subsequent re-exports leave the bid alone.
    ///
    /// Worksheet has already been updated by the time this runs, so deleted-fixture rows
    /// are strikethrough'd but still carry their pre-deletion J/K/M pricing — the lookup
    /// includes them on purpose, so a fixture deleted between B12-set and first re-export
    /// still gets a real bid price frozen against it.
    /// </summary>
    private static void ReconcileBaselinePricing(IXLWorkbook wb)
    {
        var baseline = ResolveBaselineSheet(wb);
        if (baseline == null) return;

        var stateCell = baseline.Cell(CsPriceStateRow, CsColFrozenMeta);
        var state = stateCell.GetString();
        if (!string.Equals(state, "UNPRICED", StringComparison.OrdinalIgnoreCase)) return;

        if (!wb.Worksheets.TryGetWorksheet("Worksheet", out var wsSheet)) return;

        // (Type, Catalog) → Worksheet row, INCLUDING strikethrough rows so deleted fixtures
        // can still contribute their pre-deletion bid price.
        var wsRowByKey = new Dictionary<(string T, string C), int>();
        var catalogCanonicalRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var typeFirstRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int wsLast = wsSheet.LastRowUsed()?.RowNumber() ?? 1;
        for (int r = 2; r <= wsLast; r++)
        {
            string type = wsSheet.Cell(r, WsColType).GetString();
            string cat = wsSheet.Cell(r, WsColCatalog).GetString();
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(cat)) continue;
            var key = (type.ToUpperInvariant(), cat.ToUpperInvariant());
            if (!wsRowByKey.ContainsKey(key)) wsRowByKey.Add(key, r);
            if (!catalogCanonicalRow.ContainsKey(cat) && !wsSheet.Cell(r, WsColUnitCost).HasFormula)
                catalogCanonicalRow[cat] = r;
            if (!typeFirstRow.ContainsKey(type)) typeFirstRow[type] = r;
        }

        // Refresh per-row Sell Ea. by matching on the snapshot's first non-blank catalog —
        // same canonicalization BuildCountsSheet uses when freezing prices originally.
        int blLast = baseline.LastRowUsed()?.RowNumber() ?? 1;
        for (int r = 2; r <= blLast; r++)
        {
            string type = baseline.Cell(r, CsColType).GetString();
            if (string.IsNullOrWhiteSpace(type)) continue;
            for (int c = 0; c < 6; c++)
            {
                string cn = baseline.Cell(r, CsColCat1 + c).GetString();
                if (string.IsNullOrWhiteSpace(cn)) continue;
                var key = (type.ToUpperInvariant(), cn.ToUpperInvariant());
                if (!wsRowByKey.TryGetValue(key, out int wsRow)) continue;

                // Unit Cost from canonical row; markup / adder are per-row literals.
                int costRow = catalogCanonicalRow.TryGetValue(cn, out int canon) ? canon : wsRow;
                wsSheet.Cell(costRow, WsColUnitCost).TryGetValue(out double uc);
                wsSheet.Cell(wsRow, WsColMarkup).TryGetValue(out double mk);
                wsSheet.Cell(wsRow, WsColAdder).TryGetValue(out double ad);
                double sellEa = (uc * (1 + mk)) + ad;
                if (sellEa != 0)
                {
                    baseline.Cell(r, CsColSellEa1 + c).Value = sellEa;
                    baseline.Cell(r, CsColSellEa1 + c).Style.NumberFormat.Format = "$#,##0.00";
                }
            }

            // Refresh per-Type tariff % from Worksheet col L (canonical row). Broadcast to every
            // snapshot row of this Type so Bid Compare's static B5 can read it per-row.
            double tariff = 0;
            if (typeFirstRow.TryGetValue(type, out int tRow))
                wsSheet.Cell(tRow, WsColTariff).TryGetValue(out tariff);
            baseline.Cell(r, CsColTariffPct).Value = tariff;
            baseline.Cell(r, CsColTariffPct).Style.NumberFormat.Format = "0.00%";
        }

        // Refresh frozen Dashboard meta (V1/V2/V3) from current Dashboard values.
        if (wb.Worksheets.TryGetWorksheet("Dashboard", out var dashWs))
        {
            var lutronCell = dashWs.Cell(DashLutronCell);
            var freightCell = dashWs.Cell(DashFreightSellCell);
            var taxCell = dashWs.Cell(DashSalesTaxCell);
            if (!lutronCell.IsEmpty() && lutronCell.TryGetValue(out double lutronVal))
            {
                baseline.Cell(1, CsColFrozenMeta).Value = lutronVal;
                baseline.Cell(1, CsColFrozenMeta).Style.NumberFormat.Format = "$#,##0.00";
            }
            if (!freightCell.IsEmpty() && freightCell.TryGetValue(out double freightVal))
            {
                baseline.Cell(2, CsColFrozenMeta).Value = freightVal;
                baseline.Cell(2, CsColFrozenMeta).Style.NumberFormat.Format = "$#,##0.00";
            }
            if (!taxCell.IsEmpty() && taxCell.TryGetValue(out double taxVal))
            {
                baseline.Cell(3, CsColFrozenMeta).Value = taxVal;
                baseline.Cell(3, CsColFrozenMeta).Style.NumberFormat.Format = "0.00%";
            }
        }

        baseline.Cell(CsPriceStateRow, CsColFrozenMeta).Value = "PRICED";
    }

    #endregion

    #region Print Sheets (Quote, Phase 1/2/3)

    /// <summary>
    /// Builds the Quote print sheet as a thin consumer of Worksheet helper columns AA-AG.
    /// 7 single-cell ANCHORARRAY formulas at row 7. All pipeline logic (filter, gap rows,
    /// tariff rows, subtotal/freight/grand-total footer) lives on Worksheet — this sheet is
    /// print formatting only.
    /// </summary>
    private static void RebuildContractorSheets(
        IXLWorkbook wb,
        List<CountsFixtureModel> currentFixtures,
        Dictionary<(string Type, string Catalog), WorksheetRowData>? currentPricing)
    {
        var names = new[] { "Quote", "Phase 1", "Phase 2", "Phase 3", "Bid Compare" };
        var positions = new Dictionary<string, int>();
        foreach (var name in names)
        {
            if (wb.Worksheets.TryGetWorksheet(name, out var existing))
            {
                positions[name] = existing.Position;
                existing.Delete();
            }
        }
        // Fallback anchor for sheets being created for the first time (e.g. Bid Compare on
        // the first GenerateUpdate that has a baseline): the first Counts snapshot's
        // position, so contractor sheets land before snapshots rather than at workbook end.
        int? fallbackAnchor = null;
        foreach (var w in wb.Worksheets)
        {
            if (w.Name.StartsWith("Counts ", StringComparison.OrdinalIgnoreCase)
                && (fallbackAnchor == null || w.Position < fallbackAnchor))
            {
                fallbackAnchor = w.Position;
            }
        }

        BuildQuoteSheet(wb);
        for (int p = 1; p <= 3; p++)
            BuildPhaseQuoteSheet(wb, p);
        // Lock the bid before diffing: if B12 references a born-unpriced snapshot, this
        // refreshes its frozen prices from current Worksheet/Dashboard so Bid Compare diffs
        // against finalized pricing instead of $0s.
        ReconcileBaselinePricing(wb);
        BuildBidCompareSheet(wb, currentFixtures, currentPricing);
        // Restore positions. Insert each sheet at the anchor (smallest saved position; or
        // first-Counts fallback when no contractor sheet had a prior position) in reverse
        // name order — each insertion pushes previously placed sheets one slot right,
        // yielding Quote|Phase 1|Phase 2|Phase 3|Bid Compare.
        int? anchor = positions.Count > 0 ? positions.Values.Min() : fallbackAnchor;
        if (anchor != null)
        {
            foreach (var name in names.Reverse())
            {
                if (wb.Worksheets.TryGetWorksheet(name, out var ws))
                    ws.Position = anchor.Value;
            }
        }
    }

    private static void BuildQuoteSheet(IXLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Quote");
        if (!wb.Worksheets.TryGetWorksheet("Worksheet", out var wsSheet))
            return;

        ApplyPrintSheetDefaults(ws);

        WritePrintSheetTitle(ws, 9, $"\"PRODUCT PRICING \"&Cover!{CoverDateCell}");
        ws.TabColor = XLColor.FromHtml("#FF8ED973");

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

    // Bid Compare row tints (kept for visual cues in the static diff table).
    private static readonly XLColor BcQtyTint = XLColor.FromHtml("#FFF6CE");        // pale yellow
    private static readonly XLColor BcPriceUpTint = XLColor.FromHtml("#FBD9D5");    // pale red
    private static readonly XLColor BcPriceDownTint = XLColor.FromHtml("#D9F2D9");  // pale green
    private static readonly XLColor BcAddedTint = XLColor.FromHtml("#E5F5E5");      // pale green

    private sealed class BcRow
    {
        public string Type = "";
        public string Mfr = "";
        public string Catalog = "";
        public int Slot;
        public double Qty;
        public double SellEa;
    }

    private sealed class BcDiff
    {
        public string Type = "";
        public string OldMfr = "";
        public string NewMfr = "";
        public string OldCatalog = "";
        public string NewCatalog = "";
        public string Change = "";
        public int Slot;
        public double? OldQty;
        public double? NewQty;
        public double? OldSellEa;
        public int? CurrentWsRow; // null when no current side (Removed)
    }

    /// <summary>
    /// Builds the Bid Compare sheet as a full mirror of the Worksheet's per-(Type, Catalog)
    /// rows (union of baseline ∪ current keys), with live current-side formulas and
    /// conditional formatting that re-tints rows as Worksheet pricing edits propagate.
    /// Slim 9-col layout (A-I) with two hidden support cols (J=Old Qty, K=Old Sell Ea)
    /// the Δ formulas and CF rules reference. Skipped entirely when B12 is empty or its
    /// snapshot is missing.
    /// </summary>
    private static void BuildBidCompareSheet(
        IXLWorkbook wb,
        List<CountsFixtureModel> currentFixtures,
        Dictionary<(string Type, string Catalog), WorksheetRowData>? currentPricing)
    {
        var baseline = ResolveBaselineSheet(wb);
        if (baseline == null) return;
        if (!wb.Worksheets.TryGetWorksheet("Worksheet", out var wsSheet)) return;

        // 1. Read baseline → per-(TypeUpper, CatalogUpper) BcRow.
        var baselineRows = new Dictionary<(string T, string C), BcRow>();
        int blLast = baseline.LastRowUsed()?.RowNumber() ?? 1;
        for (int r = 2; r <= blLast; r++)
        {
            string type = baseline.Cell(r, CsColType).GetString();
            if (string.IsNullOrWhiteSpace(type)) continue;
            string mfr = baseline.Cell(r, CsColMfr).GetString();
            baseline.Cell(r, CsColCount).TryGetValue(out double qty);
            for (int c = 0; c < 6; c++)
            {
                string cat = baseline.Cell(r, CsColCat1 + c).GetString();
                if (string.IsNullOrWhiteSpace(cat)) continue;
                baseline.Cell(r, CsColSellEa1 + c).TryGetValue(out double slotSellEa);
                var key = (type.ToUpperInvariant(), cat.ToUpperInvariant());
                if (!baselineRows.ContainsKey(key))
                    baselineRows.Add(key, new BcRow
                    {
                        Type = type, Mfr = mfr, Catalog = cat, Slot = c, Qty = qty, SellEa = slotSellEa,
                    });
            }
        }

        baseline.Cell(1, CsColFrozenMeta).TryGetValue(out double bidLutron);
        baseline.Cell(2, CsColFrozenMeta).TryGetValue(out double bidFreight);
        baseline.Cell(3, CsColFrozenMeta).TryGetValue(out double bidTaxRate);

        // 2. Build current per-(Type, Catalog) BcRow + Worksheet row map.
        var currentRows = new Dictionary<(string T, string C), BcRow>();
        foreach (var f in currentFixtures)
        {
            for (int c = 0; c < 6; c++)
            {
                string cat = f.CatalogNumbers[c] ?? "";
                if (string.IsNullOrWhiteSpace(cat)) continue;
                var key = (f.TypeMark.ToUpperInvariant(), cat.ToUpperInvariant());
                if (currentRows.ContainsKey(key)) continue;
                double curSellEa = 0;
                if (currentPricing != null
                    && currentPricing.TryGetValue((f.TypeMark, cat), out var p))
                {
                    double uc = p.UnitCost ?? 0, mk = p.Markup ?? 0, ad = p.Adder ?? 0;
                    curSellEa = (uc * (1 + mk)) + ad;
                }
                currentRows.Add(key, new BcRow
                {
                    Type = f.TypeMark, Mfr = f.Manufacturer, Catalog = cat, Slot = c,
                    Qty = f.Count, SellEa = curSellEa,
                });
            }
        }

        var wsRowByKey = new Dictionary<(string T, string C), int>();
        int wsLast = wsSheet.LastRowUsed()?.RowNumber() ?? 1;
        for (int r = 2; r <= wsLast; r++)
        {
            string type = wsSheet.Cell(r, WsColType).GetString();
            string cat = wsSheet.Cell(r, WsColCatalog).GetString();
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(cat)) continue;
            if (wsSheet.Cell(r, WsColType).Style.Font.Strikethrough) continue;
            var key = (type.ToUpperInvariant(), cat.ToUpperInvariant());
            if (!wsRowByKey.ContainsKey(key)) wsRowByKey.Add(key, r);
        }

        // 3. Build the full union row list (every baseline + current key — no skip).
        var allKeys = new HashSet<(string, string)>(baselineRows.Keys);
        foreach (var k in currentRows.Keys) allKeys.Add(k);

        var rows = new List<BcDiff>();
        foreach (var key in allKeys)
        {
            baselineRows.TryGetValue(key, out var bRow);
            currentRows.TryGetValue(key, out var cRow);
            wsRowByKey.TryGetValue(key, out int wsRow);

            string change;
            if (bRow == null) change = "Added";
            else if (cRow == null) change = "Removed";
            else if (!string.Equals(bRow.Mfr, cRow.Mfr, StringComparison.OrdinalIgnoreCase)) change = "Spec Δ";
            else change = ""; // unchanged at export; live deltas may still surface via CF

            rows.Add(new BcDiff
            {
                Type = (cRow ?? bRow!).Type,
                OldMfr = bRow?.Mfr ?? "", NewMfr = cRow?.Mfr ?? "",
                OldCatalog = bRow?.Catalog ?? "", NewCatalog = cRow?.Catalog ?? "",
                Change = change, Slot = (cRow ?? bRow!).Slot,
                OldQty = bRow?.Qty, NewQty = cRow?.Qty,
                OldSellEa = bRow?.SellEa,
                CurrentWsRow = wsRow == 0 ? null : wsRow,
            });
        }

        rows.Sort((a, b) =>
        {
            int cmp = NaturalStringComparer.OrdinalIgnoreCase.Compare(a.Type, b.Type);
            return cmp != 0 ? cmp : a.Slot.CompareTo(b.Slot);
        });

        // 4. Bid Total static. Subtotal = line items + per-Type tariff allocation; Lutron and
        //    Freight are added AFTER tax (matches Quote's LIGHTING PACKAGE TOTAL formula —
        //    sub × (1+tax) + Lutron + N(Freight)). Tariff % is read from the snapshot's AC
        //    column (broadcast per-row at snapshot time).
        double bidPreAdj = 0;
        double bidTariff = 0;
        for (int r = 2; r <= blLast; r++)
        {
            string type = baseline.Cell(r, CsColType).GetString();
            if (string.IsNullOrWhiteSpace(type)) continue;
            baseline.Cell(r, CsColCount).TryGetValue(out double q);
            double rowSellSum = 0;
            for (int c = 0; c < 6; c++)
            {
                baseline.Cell(r, CsColSellEa1 + c).TryGetValue(out double slot);
                rowSellSum += slot;
            }
            double rowSellExt = q * rowSellSum;
            bidPreAdj += rowSellExt;
            baseline.Cell(r, CsColTariffPct).TryGetValue(out double rowTariff);
            bidTariff += rowSellExt * rowTariff;
        }
        double bidSubtotal = bidPreAdj + bidTariff;
        double bidTotalIncl = bidSubtotal * (1.0 + bidTaxRate) + bidLutron + bidFreight;

        // 5. Emit sheet.
        var ws = wb.Worksheets.Add("Bid Compare");
        ws.TabColor = XLColor.FromHtml("#FF8ED973");
        ws.Style.Font.FontName = "Segoe UI";
        ws.Style.Font.FontSize = 11;

        // Match Quote's sellSubtotal: Σ(line items) + Σ(sellExt × per-row tariff%). Per-row
        // tariff is broadcast from each Type's canonical L row via XLOOKUP, mirroring Quote.
        string activeFlag = $"(Worksheet!AG2:AG{wsLast}=1)";
        string sellEaPerRow = $"IFERROR((Worksheet!J2:J{wsLast}*(1+Worksheet!K2:K{wsLast}))+Worksheet!M2:M{wsLast},0)";
        string effQty = $"Worksheet!BR2:BR{wsLast}";
        string tariffPerRow = $"IFERROR(_xlfn.XLOOKUP(Worksheet!A2:A{wsLast},Worksheet!A2:A{wsLast},Worksheet!L2:L{wsLast}),0)";
        string subFormula =
            $"SUMPRODUCT({activeFlag}*{sellEaPerRow}*{effQty})"
            + $"+SUMPRODUCT({activeFlag}*{sellEaPerRow}*{effQty}*{tariffPerRow})";
        string lutronExpr = "IF(LutronSubtotal=\"\",0,LutronSubtotal)";
        string freightExpr = "IF(FreightSell=\"\",0,FreightSell)";
        string taxExpr = $"IF(Dashboard!{DashSalesTaxCell}=\"\",0,Dashboard!{DashSalesTaxCell})";

        // Metadata block lives on rows 1–5 in two halves: Bid side in cols E/F, Current side
        // in cols H/I. Bid date is shown via the "Compared against:" footer below (no dedicated
        // row). Col I also carries ΔExt data starting at row 8 (column-level format applied
        // later is +$/-$); the metadata cells in rows 1–4 override per-cell to plain currency /
        // percent, and only Net Change (I5) inherits the +/- column default.
        void WriteLabel(string addr, string text)
        {
            var c = ws.Cell(addr);
            c.Value = text;
            c.Style.Font.Bold = true;
            c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }

        WriteLabel("E1", "Bid Lutron:");
        ws.Cell("F1").Value = bidLutron;
        ws.Cell("F1").Style.NumberFormat.Format = "$#,##0.00";
        WriteLabel("E2", "Bid Freight:");
        ws.Cell("F2").Value = bidFreight;
        ws.Cell("F2").Style.NumberFormat.Format = "$#,##0.00";
        WriteLabel("E3", "Bid Sales Tax:");
        ws.Cell("F3").Value = bidTaxRate;
        ws.Cell("F3").Style.NumberFormat.Format = "0.00%";
        WriteLabel("E4", "Bid Total (incl tax):");
        ws.Cell("F4").Value = bidTotalIncl;
        ws.Cell("F4").Style.NumberFormat.Format = "$#,##0.00;($#,##0.00)";

        WriteLabel("H1", "Current Lutron:");
        ws.Cell("I1").FormulaA1 = lutronExpr;
        ws.Cell("I1").Style.NumberFormat.Format = "$#,##0.00";
        WriteLabel("H2", "Current Freight:");
        ws.Cell("I2").FormulaA1 = freightExpr;
        ws.Cell("I2").Style.NumberFormat.Format = "$#,##0.00";
        WriteLabel("H3", "Current Sales Tax:");
        ws.Cell("I3").FormulaA1 = taxExpr;
        ws.Cell("I3").Style.NumberFormat.Format = "0.00%";
        WriteLabel("H4", "Current Total (incl tax):");
        // sub × (1+tax) + Lutron + Freight — Lutron / Freight are NOT taxed, matching Quote.
        ws.Cell("I4").FormulaA1 =
            $"({subFormula})*(1+({taxExpr}))+({lutronExpr})+({freightExpr})";
        ws.Cell("I4").Style.NumberFormat.Format = "$#,##0.00;($#,##0.00)";

        WriteLabel("H5", "Net Change:");
        ws.Cell("I5").FormulaA1 = "I4-F4";
        ws.Cell("I5").Style.NumberFormat.Format = "+$#,##0.00;-$#,##0.00;;@";
        ws.Cell("I5").Style.Font.Bold = true;

        // Header row — slim 9-col + 2 hidden support cols.
        int headerRow = 7;
        string[] headers = {
            "Type", "Mfr", "Catalog",
            "Qty", "ΔQty",
            "Sell Ea.", "ΔSell Ea.",
            "Ext.", "ΔExt.",
            "Old Qty", "Old Sell Ea.",
        };
        for (int i = 0; i < headers.Length; i++)
        {
            var c = ws.Cell(headerRow, i + 1);
            c.Value = headers[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = XLColor.FromHtml("#262626");
            c.Style.Font.FontColor = XLColor.FromHtml("#FACC75");
            c.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        // Data rows.
        int firstDataRow = headerRow + 1;
        int row = firstDataRow;
        foreach (var d in rows)
        {
            int wsRow = d.CurrentWsRow ?? 0;

            ws.Cell(row, 1).Value = d.Type;

            // Mfr — same UPPER + Environmental Lights → LUMEN SPEC normalization Quote uses,
            // since Bid Compare may be shown to a contractor. Stacked old\nnew on Spec Δ;
            // static for Removed/Added; live formula for matched.
            if (d.Change == "Spec Δ" && !string.Equals(d.OldMfr, d.NewMfr, StringComparison.OrdinalIgnoreCase))
            {
                ws.Cell(row, 2).Value = $"{NormalizeMfrDisplay(d.OldMfr)}\n{NormalizeMfrDisplay(d.NewMfr)}";
                ws.Cell(row, 2).Style.Alignment.WrapText = true;
            }
            else if (d.Change == "Removed")
            {
                ws.Cell(row, 2).Value = NormalizeMfrDisplay(d.OldMfr);
            }
            else if (wsRow > 0)
            {
                string anchor = $"(IF(Worksheet!U{wsRow}=\"\",Worksheet!B{wsRow},Worksheet!U{wsRow}))";
                ws.Cell(row, 2).FormulaA1 = BuildMfrDisplayFormula(anchor);
            }
            else
            {
                ws.Cell(row, 2).Value = NormalizeMfrDisplay(d.NewMfr);
            }

            // Catalog — live for matched/added rows; static for Removed.
            if (wsRow > 0)
                ws.Cell(row, 3).FormulaA1 = $"Worksheet!C{wsRow}";
            else
                ws.Cell(row, 3).Value = d.Change == "Removed" ? d.OldCatalog : d.NewCatalog;

            // Qty (D) — live for matched/added; static old for Removed.
            if (wsRow > 0)
                ws.Cell(row, 4).FormulaA1 = $"Worksheet!BR{wsRow}";
            else if (d.Change == "Removed" && d.OldQty.HasValue)
                ws.Cell(row, 4).Value = d.OldQty.Value;
            else if (d.NewQty.HasValue)
                ws.Cell(row, 4).Value = d.NewQty.Value;

            // ΔQty (E).
            ws.Cell(row, 5).FormulaA1 = $"IF(ISNUMBER(D{row}),D{row},0)-IF(ISNUMBER(J{row}),J{row},0)";

            // Sell Ea (F).
            if (wsRow > 0)
                ws.Cell(row, 6).FormulaA1 =
                    $"IFERROR((Worksheet!J{wsRow}*(1+Worksheet!K{wsRow}))+Worksheet!M{wsRow},0)";
            else if (d.Change == "Removed" && d.OldSellEa.HasValue)
                ws.Cell(row, 6).Value = d.OldSellEa.Value;

            // ΔSell (G).
            ws.Cell(row, 7).FormulaA1 = $"IF(ISNUMBER(F{row}),F{row},0)-IF(ISNUMBER(K{row}),K{row},0)";

            // Ext (H).
            ws.Cell(row, 8).FormulaA1 = $"IFERROR(D{row}*F{row},0)";

            // ΔExt (I).
            ws.Cell(row, 9).FormulaA1 =
                $"H{row}-IF(AND(ISNUMBER(J{row}),ISNUMBER(K{row})),J{row}*K{row},0)";

            // Hidden support cols.
            if (d.OldQty.HasValue) ws.Cell(row, 10).Value = d.OldQty.Value;
            if (d.OldSellEa.HasValue) ws.Cell(row, 11).Value = d.OldSellEa.Value;

            if (d.Change == "Removed")
            {
                ws.Range(row, 1, row, 9).Style.Font.Strikethrough = true;
                ws.Range(row, 1, row, 9).Style.Font.FontColor = XLColor.FromHtml("#808080");
            }

            row++;
        }
        int lastDataRow = row - 1;

        // 6. Conditional formatting — re-evaluates as Worksheet edits propagate.
        // J/K (hidden) hold baseline qty/sell. Empty J & K → no baseline (Added). Strikethrough
        // (Removed) rows already have a baked gray font; CF won't repaint them.
        if (lastDataRow >= firstDataRow)
        {
            var dataRange = ws.Range(firstDataRow, 1, lastDataRow, 9);

            // Added (no baseline at all).
            dataRange.AddConditionalFormat()
                .WhenIsTrue($"AND($J{firstDataRow}=\"\",$K{firstDataRow}=\"\")")
                .Fill.SetBackgroundColor(BcAddedTint);

            // Price up.
            dataRange.AddConditionalFormat()
                .WhenIsTrue($"AND(ISNUMBER($K{firstDataRow}),ISNUMBER($F{firstDataRow}),$F{firstDataRow}-$K{firstDataRow}>0.005)")
                .Fill.SetBackgroundColor(BcPriceUpTint);

            // Price down.
            dataRange.AddConditionalFormat()
                .WhenIsTrue($"AND(ISNUMBER($K{firstDataRow}),ISNUMBER($F{firstDataRow}),$K{firstDataRow}-$F{firstDataRow}>0.005)")
                .Fill.SetBackgroundColor(BcPriceDownTint);

            // Qty Δ (lower priority — only paints when no price tint above matched).
            dataRange.AddConditionalFormat()
                .WhenIsTrue($"AND(ISNUMBER($J{firstDataRow}),ISNUMBER($D{firstDataRow}),$D{firstDataRow}<>$J{firstDataRow})")
                .Fill.SetBackgroundColor(BcQtyTint);
        }

        // Number formats / alignment.
        ws.Column(2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        for (int c = 4; c <= 9; c++)
            ws.Column(c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws.Column(5).Style.NumberFormat.Format = "+0;-0;;@";
        ws.Column(6).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(7).Style.NumberFormat.Format = "+$#,##0.00;-$#,##0.00;;@";
        ws.Column(8).Style.NumberFormat.Format = "$#,##0.00;($#,##0.00)";
        ws.Column(9).Style.NumberFormat.Format = "+$#,##0.00;-$#,##0.00;;@";

        // Re-apply per-cell formats on the metadata block — column-level format above propagates
        // through to all existing cells in the column, including rows 1–5. F (col 6) gets plain
        // currency; I (col 9) gets +$/-$ for ΔExt data, which is wrong for the Lutron/Freight/Tax/
        // Total metadata in rows 1–4 (only Net Change in I5 should inherit the +/- default).
        ws.Cell("F3").Style.NumberFormat.Format = "0.00%";
        ws.Cell("F4").Style.NumberFormat.Format = "$#,##0.00;($#,##0.00)";
        ws.Cell("I1").Style.NumberFormat.Format = "$#,##0.00";
        ws.Cell("I2").Style.NumberFormat.Format = "$#,##0.00";
        ws.Cell("I3").Style.NumberFormat.Format = "0.00%";
        ws.Cell("I4").Style.NumberFormat.Format = "$#,##0.00;($#,##0.00)";

        // Column widths. Col A (Type) mirrors Worksheet's already-measured Type width.
        // Mfr/Catalog get a measurement pass via AdjustToContents, with a small additive
        // buffer to compensate for ClosedXML's known under-measurement on non-Calibri fonts
        // (Segoe UI 11 here). Without the buffer, long Mfr names and catalog numbers clip.
        // Metadata labels in cols E and H overflow left into empty cols D / G.
        ws.Column(1).Width = Math.Max(7, wsSheet.Column(WsColType).Width);
        ws.Column(2).AdjustToContents();
        ws.Column(3).AdjustToContents();
        ws.Column(2).Width += 3;
        ws.Column(3).Width += 3;
        for (int c = 4; c <= 5; c++) ws.Column(c).Width = 8;
        for (int c = 6; c <= 9; c++) ws.Column(c).Width = 13;

        ws.Column(10).Hide();
        ws.Column(11).Hide();

        // Type-group dividers — matches Worksheet style (thin #262626 bottom border).
        var divColor = XLColor.FromHtml("#262626");
        for (int r = firstDataRow; r < lastDataRow; r++)
        {
            string thisType = ws.Cell(r, 1).GetString();
            string nextType = ws.Cell(r + 1, 1).GetString();
            if (string.Equals(thisType, nextType, StringComparison.OrdinalIgnoreCase))
                continue;
            var rng = ws.Range(r, 1, r, 9);
            rng.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            rng.Style.Border.BottomBorderColor = divColor;
        }

        ApplyStandardPageSetup(ws);
        ws.PageSetup.SetRowsToRepeatAtTop(headerRow, headerRow);
    }

    /// <summary>Returns Dashboard!B12 as a DateTime, or null when blank/unparseable.</summary>
    private static DateTime? ReadBidDate(IXLWorkbook wb)
    {
        if (!wb.Worksheets.TryGetWorksheet("Dashboard", out var dashWs)) return null;
        var b11 = dashWs.Cell(DashBidDateCell);
        if (b11.IsEmpty()) return null;
        if (b11.TryGetValue(out DateTime dt)) return dt;
        return null;
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

        WritePrintSheetTitle(ws, 8, $"\"PHASE {phase} PRODUCT PRICING \"&Cover!{CoverDateCell}");
        ws.TabColor = XLColor.FromHtml("#FF8ED973");

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

    /// <summary>C# mirror of <see cref="BuildMfrDisplayFormula"/> for static Mfr values
    /// (Removed/Added/Spec Δ rows on Bid Compare). Also applies <see cref="TrimMfrForDisplay"/>
    /// so static rows match the trimmed form Worksheet col B holds for live rows.</summary>
    private static string NormalizeMfrDisplay(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        string trimmed = TrimMfrForDisplay(raw);
        string upper = trimmed.ToUpperInvariant();
        return upper == "ENVIRONMENTAL LIGHTS" ? "LUMEN SPEC" : upper;
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
            .WhenIsTrue($"SUMPRODUCT((TRIM(QuoteNotes)=TRIM({firstCell}))*((QuoteNotesBold=TRUE)+(QuoteNotesBold=\"TRUE\")))>0")
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
        ws.Cell("A1").FormulaA1 = $"Cover!{CoverProjectNameCell}";
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

        // Bold + right-align any Qty-column cell that equals one of the footer labels.
        // Sales Tax label includes the rate inline ("Sales Tax (8.05%):"), so match by prefix.
        string labelPredicate = string.Join(",", labels.Select(l => $"${qtyLetter}{spillRow}=\"{l}\""));
        labelPredicate += $",LEFT(${qtyLetter}{spillRow},10)=\"Sales Tax \"";
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
        ws.TabColor = XLColor.FromHtml("#FFF28B82");

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
        string sub = "ws.start";
        try
        {
            UpdateWorksheetSheetCore(wb, fixtures, countsSheetName, existingRows, prevData, ref sub);
        }
        catch (Exception ex) when (!(ex is InvalidOperationException && ex.Message.StartsWith("[ws-stage=")))
        {
            throw new InvalidOperationException(
                $"[ws-stage={sub}] {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    private static void UpdateWorksheetSheetCore(
        IXLWorkbook wb,
        List<CountsFixtureModel> fixtures,
        string countsSheetName,
        List<WorksheetRowData> existingRows,
        Dictionary<string, CountsFixtureModel>? prevData,
        ref string sub)
    {
        sub = "get-worksheet";
        if (!wb.Worksheets.TryGetWorksheet("Worksheet", out var ws))
        {
            BuildWorksheetSheet(wb, fixtures, countsSheetName, null);
            return;
        }

        string csRef = $"'{countsSheetName}'";

        sub = "build-new-keys";
        // Build set of new (Type, Catalog) pairs. Length-token templates expand here so each
        // resolved SKU gets its own row entry; the (Type, ResolvedSku) cache key gives every
        // cut its own pricing/PrevQty without leaking across lengths.
        // SortLength is the cut-inches when the slot expanded (used for stable ascending sort
        // within a slot), or int.MinValue for plain templates (single row per slot).
        var newTypeMarks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newKeys = new HashSet<(string, string)>();
        var newRowEntries = new List<(string Type, string Mfr, string Catalog, int CatPosition, int SortLength, int Qty, bool IsExpanded)>();
        var fixtureByType = new Dictionary<string, CountsFixtureModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fixtures)
        {
            newTypeMarks.Add(f.TypeMark);
            fixtureByType.TryAdd(f.TypeMark, f);
            for (int c = 0; c < 6; c++)
            {
                string template = f.CatalogNumbers[c] ?? "";
                if (string.IsNullOrWhiteSpace(template)) continue;
                bool isExpanded = CatalogLengthTokenResolver.HasToken(template);
                int? maxInches = isExpanded ? CatalogLengthTokenResolver.ParseMaxInches(template) : null;

                if (isExpanded)
                {
                    // Re-derive (sku, qty, cutInches) so we can drive the in-slot sort by cut length.
                    var pooled = new Dictionary<int, int>();
                    foreach (var kv in f.LinearLengthBuckets)
                        foreach (int cut in CatalogLengthTokenResolver.SplitInstance(kv.Key, maxInches))
                        {
                            pooled.TryGetValue(cut, out var n);
                            pooled[cut] = n + kv.Value;
                        }
                    foreach (var kv in pooled.OrderBy(p => p.Key))
                    {
                        string sku = CatalogLengthTokenResolver.Resolve(template, kv.Key);
                        var key = (f.TypeMark.ToUpperInvariant(), sku.ToUpperInvariant());
                        newKeys.Add(key);
                        newRowEntries.Add((f.TypeMark, f.Manufacturer, sku, c, kv.Key, kv.Value, true));
                    }
                }
                else
                {
                    var key = (f.TypeMark.ToUpperInvariant(), template.ToUpperInvariant());
                    newKeys.Add(key);
                    newRowEntries.Add((f.TypeMark, f.Manufacturer, template, c, int.MinValue, 0, false));
                }
            }
        }

        // Drop rows that were marked removed (strikethrough) on a prior update and are still
        // not in the new data. They've had one update to come back; if they didn't, retire them
        // permanently. Resurrected ones (key now in newKeys) fall through and get treated as
        // matched rows with the strikethrough/red-fill cleared by the styling reset below.
        // Captured from WorksheetRowData.IsStrikethrough — must read it before the worksheet
        // styling is cleared, since that wipes the strikethrough flag in the cells themselves.
        existingRows = existingRows
            .Where(er => !er.IsStrikethrough || newKeys.Contains((er.Type.ToUpperInvariant(), er.Catalog.ToUpperInvariant())))
            .ToList();

        // Build lookup of (filtered) existing rows by (Type, Catalog)
        var existingByKey = new Dictionary<(string, string), WorksheetRowData>();
        foreach (var er in existingRows)
        {
            var key = (er.Type.ToUpperInvariant(), er.Catalog.ToUpperInvariant());
            existingByKey.TryAdd(key, er);
        }

        // Determine which existing types existed before (from prev data)
        var prevTypeMarks = prevData?.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        sub = "unprotect.call";
        ws.Unprotect();

        sub = "unprotect.lastRow1";
        int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        if (lastRow > 1)
        {
            sub = $"unprotect.styleReset(lastRow={lastRow})";
            var dataRange = ws.Range(2, 1, lastRow, WsColQtyOverride);
            dataRange.Style.Fill.BackgroundColor = XLColor.NoColor;
            dataRange.Style.Font.Strikethrough = false;
        }

        sub = "unprotect.lastRow2";
        lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        // Clear contents/formats on rows 2..lastRow rather than deleting rows. Once Excel
        // opens the workbook it materializes the row-2 dynamic-array spills (BT2:CC2 etc.)
        // and tags every spilled cell with array-formula metadata; ClosedXML's Row.Delete
        // then trips an IndexOutOfRangeException trying to fix up references. Clearing the
        // range avoids the delete path entirely and lets the row-write loop overwrite from
        // row 2 onward.
        if (lastRow >= 2)
        {
            sub = $"unprotect.clearRange(2..{lastRow})";
            ws.Range(2, 1, lastRow, WsColHelperLast).Clear(
                XLClearOptions.Contents | XLClearOptions.NormalFormats | XLClearOptions.ConditionalFormats | XLClearOptions.Comments);
        }

        sub = "sort-new-rows";
        // Step 3: Write all rows sorted by Type Mark, then catalog position, then by SortLength
        // (cut inches ascending within a length-token slot; plain slots use int.MinValue so
        // their single row sorts before any exploded sibling — which doesn't happen since
        // expansion is per-slot, but keeps the order deterministic).
        newRowEntries.Sort((a, b) =>
        {
            int cmp = string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            cmp = a.CatPosition.CompareTo(b.CatPosition);
            return cmp != 0 ? cmp : a.SortLength.CompareTo(b.SortLength);
        });

        // Also include removed rows (in existing but not in new)
        var removedEntries = new List<(string Type, string Mfr, string Catalog)>();
        foreach (var er in existingRows)
        {
            var key = (er.Type.ToUpperInvariant(), er.Catalog.ToUpperInvariant());
            if (!newKeys.Contains(key))
                removedEntries.Add((er.Type, er.Mfr, er.Catalog));
        }

        // Count how many rows each catalog appears on — only mark canonical when siblings exist.
        // Keyed by resolved SKU so two Types whose templates collapse to the same SKU still bold.
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
            var entry = newRowEntries[i];
            var key = (entry.Type.ToUpperInvariant(), entry.Catalog.ToUpperInvariant());
            var existing = existingByKey.GetValueOrDefault(key);
            bool hasLiteralCost = existing != null && existing.UnitCost.HasValue && !existing.CostIsFormula;
            if (hasLiteralCost && !canonicalSheetRowByCatalog.ContainsKey(entry.Catalog))
                canonicalSheetRowByCatalog[entry.Catalog] = sheetRow;
        }
        // Fallback: for catalogs with no literal-cost row, use first occurrence
        for (int i = 0; i < newRowEntries.Count; i++)
        {
            int sheetRow = 2 + i;
            var entry = newRowEntries[i];
            if (!canonicalSheetRowByCatalog.ContainsKey(entry.Catalog))
                canonicalSheetRowByCatalog[entry.Catalog] = sheetRow;
        }

        // Type-canonical sheet row: first emitted row of each Type in sort order (tariff source).
        var typeCanonicalSheetRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < newRowEntries.Count; i++)
        {
            int sheetRow = 2 + i;
            var entry = newRowEntries[i];
            typeCanonicalSheetRow.TryAdd(entry.Type, sheetRow);
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

        sub = "write-new-rows";
        int row = 2;

        // Write new/matched rows
        foreach (var entry in newRowEntries)
        {
            string type = entry.Type;
            string mfr = entry.Mfr;
            string catalog = entry.Catalog;
            int slot = entry.CatPosition;

            var key = (type.ToUpperInvariant(), catalog.ToUpperInvariant());
            var existing = existingByKey.GetValueOrDefault(key);
            bool isNewType = !prevTypeMarks.Contains(type);
            bool isNewRow = existing == null;
            int canonicalRow = canonicalSheetRowByCatalog[catalog];

            ws.Cell(row, WsColType).Value = type;
            ws.Cell(row, WsColMfr).Value = TrimMfrForDisplay(mfr);
            ws.Cell(row, WsColCatalog).Value = catalog;
            ws.Cell(row, WsColTypeRepeat).Value = type;

            // Qty: length-token rows hold a literal cut count, QtyX rule overrides the
            // Calc-driven formula, else fall through to the formula path. Mirrors BuildWorksheetSheet.
            var fixtureForRow = fixtureByType.GetValueOrDefault(type);
            var qtyRule = fixtureForRow != null
                ? CatalogQtyParser.Parse(fixtureForRow.CatalogQtys[slot])
                : CatalogQtyRule.DefaultRule;
            if (entry.IsExpanded)
                ws.Cell(row, WsColQty).Value = entry.Qty;
            else if (qtyRule.Mode == CatalogQtyMode.Default)
                ws.Cell(row, WsColQty).FormulaA1 = BuildQtyFormula(row, csRef);
            else
                ws.Cell(row, WsColQty).Value = qtyRule.Evaluate(fixtureForRow!.Count);
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

            // Prev Qty — always the literal prior-update value. Prefer the previous Worksheet's
            // cached Qty (reflects Calc adjustments), else recompute from the preserved canonical
            // Calc + prev fixture lengths (cache may be missing on 3rd+ passes — ClosedXML doesn't
            // emit caches for rewritten formulas), else leave blank for types not previously present.
            // The B12-driven "Compare to" baseline is consumed by the Bid Compare sheet, not here.
            if (existing?.PrevQty.HasValue == true)
            {
                ws.Cell(row, WsColPrevQty).Value = existing.PrevQty.Value;
            }
            else if (!entry.IsExpanded && prevData != null && prevData.TryGetValue(type, out var prevFixture))
            {
                // Skipped for length-token rows: ComputeQtyForCalc reads the prior fixture's
                // total LinearLength/Count, which doesn't represent a single cut. Brand-new
                // length-rows leave PrevQty blank (Δ stays blank too — correct behavior).
                prevCalcByCatalog.TryGetValue(catalog, out string? prevCalc);
                ws.Cell(row, WsColPrevQty).Value = ComputeQtyForCalc(prevCalc, prevFixture);
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
                    ws.Cell(row, WsColPrevQty).Value = 0;
                    for (int col = 1; col <= WsColDelta; col++)
                        ws.Cell(row, col).Style.Fill.BackgroundColor = GreenFill;
                }
            }

            // Yellow on Type/Mfr/Catalog when this catalog isn't in prevData under this type
            // (catalog change vs the prior snapshot). Triggers in both branches above:
            //   - existing == null: brand-new worksheet row (the original "new catalog" case)
            //   - existing != null: resurrected strikethrough row, or any catalog whose key
            //     happens to match a stale row but wasn't in prevData
            // Skipped for isNewType (whole row is green) and when prevData is unavailable.
            if (!isNewType && prevData != null
                && prevData.TryGetValue(type, out var prevFix))
            {
                // Length-token rows: prevData only stores raw templates, so compare against
                // existingRows (prior Worksheet contents, keyed by resolved SKU). A brand-new
                // cut length introduced this pass has no existing row → flags yellow.
                // Plain rows: compare resolved catalog against the prior fixture's catalog list.
                bool prevHasIt = entry.IsExpanded
                    ? existing != null
                    : prevFix.CatalogNumbers.Any(c => !string.IsNullOrEmpty(c)
                        && string.Equals(c, catalog, StringComparison.OrdinalIgnoreCase));
                if (!prevHasIt)
                {
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

        sub = "write-removed-rows";
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

        sub = "apply-qty-formatting";
        ApplyQtyColumnFormatting(ws);
        sub = "apply-pricing-formats";
        ApplyPricingColumnFormats(ws);
        sub = "apply-header-styling";
        ApplyWorksheetHeaderStyling(ws);
        sub = "apply-typography";
        ApplyWorksheetTypography(ws);
        sub = "apply-row-heights";
        ApplyWorksheetRowHeights(ws);
        sub = "apply-borders";
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

        sub = "type-group-dividers";
        // Light gray divider at the last row of each Type group
        ApplyTypeGroupDividers(ws, 2, lastDataRow);

        sub = "write-helper-pipeline";
        // Helper pipeline (AA-BJ) — re-emit with updated lastDataRow bounds
        WriteHelperPipeline(ws, lastDataRow);

        sub = "hide-helper-columns";
        // Hide helper columns Z..BJ (no-op on re-runs)
        for (int col = WsColActive; col <= WsColHelperLast; col++)
            ws.Column(col).Hide();

        sub = "reapply-protection";
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
        Dictionary<string, CountsFixtureModel> prevData,
        DateTime headerDate)
    {
        string dateStr = headerDate.ToString("yyyy.MM.dd");

        var newByType = fixtures.ToDictionary(f => f.TypeMark, StringComparer.OrdinalIgnoreCase);

        // Collect this batch's rows first; we insert them at the top (newest first) once
        // we know how many there are. Insert order: Added, Removed, Changed (same as the
        // legacy append order, so within a batch the visual sequence is unchanged).
        var batch = new List<(string Type, string Change, string Old, string New)>();

        foreach (var f in fixtures)
        {
            if (!prevData.ContainsKey(f.TypeMark))
                batch.Add((f.TypeMark, "Added", "", ""));
        }

        foreach (var kvp in prevData)
        {
            if (!newByType.ContainsKey(kvp.Key))
                batch.Add((kvp.Key, "Removed", "", ""));
        }

        foreach (var f in fixtures)
        {
            if (!prevData.TryGetValue(f.TypeMark, out var prev)) continue;

            if (f.Count != prev.Count)
                batch.Add((f.TypeMark, "Qty", prev.Count.ToString(), f.Count.ToString()));

            if (f.Manufacturer != prev.Manufacturer)
                batch.Add((f.TypeMark, "Mfr", prev.Manufacturer, f.Manufacturer));

            if (Math.Abs(f.LinearLength - prev.LinearLength) > 0.01)
                batch.Add((f.TypeMark, "Linear Length",
                    prev.LinearLength.ToString("F2"), f.LinearLength.ToString("F2")));

            for (int c = 0; c < 6; c++)
            {
                string oldCat = prev.CatalogNumbers[c] ?? "";
                string newCat = f.CatalogNumbers[c] ?? "";
                if (oldCat != newCat)
                    batch.Add((f.TypeMark, $"Catalog Number {c + 1}", oldCat, newCat));
            }
        }

        if (batch.Count == 0)
            return;

        // Sheet is created on demand so it never appears with zero recorded changes.
        if (!wb.Worksheets.TryGetWorksheet("Changes", out var ws))
        {
            BuildChangesSheet(wb);
            ws = wb.Worksheet("Changes");
            // Position right before the first Counts snapshot so Changes lands between
            // Bid Compare and the snapshot block instead of at workbook end.
            int? targetPos = null;
            foreach (var w in wb.Worksheets)
            {
                if (w.Name.StartsWith("Counts ", StringComparison.OrdinalIgnoreCase)
                    && (targetPos == null || w.Position < targetPos))
                {
                    targetPos = w.Position;
                }
            }
            if (targetPos != null) ws.Position = targetPos.Value;
        }

        // Insert this batch at the top so newest updates appear first. If the sheet has
        // no prior data rows, write directly into rows 2+; otherwise push existing rows
        // down by batch.Count and write the new batch into rows 2..1+batch.Count.
        bool hadPriorData = (ws.LastRowUsed()?.RowNumber() ?? 1) >= 2;
        if (hadPriorData)
        {
            ws.Row(2).InsertRowsAbove(batch.Count);
            // InsertRowsAbove clones the format of the row above (row 1 = dark amber header
            // strip), so clear styling on the inserted block before writing data. The
            // banding/border pass below paints fresh row formatting.
            ws.Range(2, 1, 1 + batch.Count, ChangesBatchMarkerCol)
                .Clear(XLClearOptions.NormalFormats);
        }

        int row = 2;
        foreach (var (type, change, oldVal, newVal) in batch)
            WriteChangeRow(ws, ref row, dateStr, type, change, oldVal, newVal);

        // Mark the last row of this batch in the hidden marker column so the divider
        // separating this batch from older batches below can be re-drawn on every
        // subsequent append (banding wipes interior borders).
        ws.Cell(row - 1, ChangesBatchMarkerCol).Value = "|";
        ws.Column(ChangesBatchMarkerCol).Hide();

        int lastDataRow = ws.LastRowUsed()?.RowNumber() ?? (row - 1);

        // Reapply banding/borders across the full data range so newly inserted rows
        // pick up the same styling as the existing ones.
        ApplyRawSheetBandingAndBorders(ws, lastCol: 5, firstDataRow: 2, lastDataRow: lastDataRow);

        // Re-apply the thin #262626 batch separator on every marked row — including
        // prior batches whose dividers were just clobbered by the banding pass.
        for (int r = 2; r <= lastDataRow; r++)
        {
            if (ws.Cell(r, ChangesBatchMarkerCol).GetString() != "|") continue;
            var divider = ws.Range(r, 1, r, 5);
            divider.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            divider.Style.Border.BottomBorderColor = XLColor.FromHtml("#262626");
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
