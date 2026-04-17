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

    private const string DefaultPricingPath = @"C:\Path\To\PricingWorkbook.xlsx";

    // Worksheet column indices (1-based)
    private const int WsColType = 1;        // A
    private const int WsColMfr = 2;         // B
    private const int WsColCatalog = 3;     // C
    private const int WsColQty = 4;         // D
    private const int WsColPrevQty = 5;     // E
    private const int WsColDelta = 6;       // F
    private const int WsColDesc = 7;        // G
    private const int WsColCalc = 8;        // H
    private const int WsColUnitCost = 9;    // I
    private const int WsColMarkup = 10;     // J
    private const int WsColTariff = 11;     // K
    private const int WsColAdder = 12;      // L
    private const int WsColPhase = 13;      // M

    // Hidden helper pipeline columns on Worksheet. Active flag (Z) is a per-row 0/1 literal
    // written by C#; every helper spill formula filters on (Z=1) to exclude strikethrough rows.
    // AA-AG feed the Quote sheet; AI-AN / AP-AU / AW-BB feed Phase 1/2/3. AH/AO/AV are unused
    // spacers for readability. All columns Z and beyond are hidden and locked.
    private const int WsColActive = 26;     // Z
    private const string HelperFirstCol = "AA";
    private const int WsColHelperLast = 54; // BB

    private static readonly string[] QuoteHelperCols =   { "AA", "AB", "AC", "AD", "AE", "AF", "AG" };
    private static readonly string[] Phase1HelperCols =  { "AI", "AJ", "AK", "AL", "AM", "AN" };
    private static readonly string[] Phase2HelperCols =  { "AP", "AQ", "AR", "AS", "AT", "AU" };
    private static readonly string[] Phase3HelperCols =  { "AW", "AX", "AY", "AZ", "BA", "BB" };

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

    // Highlight colors
    private static readonly XLColor GreenFill = XLColor.FromHtml("#C6EFCE");
    private static readonly XLColor RedFill = XLColor.FromHtml("#FFC7CE");
    private static readonly XLColor YellowFill = XLColor.FromHtml("#FFEB9C");

    #endregion

    /// <summary>
    /// Creates a new Counts workbook with all five sheets.
    /// </summary>
    public static void GenerateNew(
        List<CountsFixtureModel> fixtures,
        string projectName,
        string outputPath)
    {
        using var wb = new XLWorkbook();

        string dateString = DateTime.Now.ToString("yyyy.MM.dd");
        string countsSheetName = $"Counts {dateString}";

        // Read pricing workbook (silently skip if unavailable)
        var pricing = ReadPricingWorkbook(DefaultPricingPath);

        // 1. Cover
        BuildCoverSheet(wb, projectName);

        // 2. Worksheet
        BuildWorksheetSheet(wb, fixtures, countsSheetName, pricing, null);

        // 3. Quote
        BuildQuoteSheet(wb);

        // 4–6. Phase sheets
        for (int p = 1; p <= 3; p++)
            BuildPhaseQuoteSheet(wb, p);

        // 7. Changes
        BuildChangesSheet(wb);

        // 8. Counts (dated)
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
    }

    /// <summary>
    /// Opens an existing workbook and updates it with fresh Revit data.
    /// </summary>
    public static void GenerateUpdate(
        List<CountsFixtureModel> fixtures,
        string existingPath)
    {
        using var wb = new XLWorkbook(existingPath);

        string dateString = DateTime.Now.ToString("yyyy.MM.dd");
        string countsSheetName = ResolveCountsSheetName(wb, dateString);

        // Find previous Counts sheet for change detection
        var prevCountsSheet = FindLatestCountsSheet(wb);
        var prevData = prevCountsSheet != null ? ReadCountsSheetData(prevCountsSheet) : null;

        // Read pricing workbook path from Cover sheet config
        string pricingPath = ReadPricingPathFromCover(wb);
        var pricing = ReadPricingWorkbook(pricingPath);

        // Read existing Worksheet rows
        var existingRows = ReadExistingWorksheetRows(wb);

        // Build new Counts sheet
        BuildCountsSheet(wb, fixtures, countsSheetName);

        // Update Worksheet
        UpdateWorksheetSheet(wb, fixtures, countsSheetName, pricing, existingRows, prevData);

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
    }

    #region Cover Sheet

    private static void BuildCoverSheet(IXLWorkbook wb, string projectName)
    {
        var ws = wb.Worksheets.Add("Cover");

        // Branding area (rows 1-4 left blank for user images)
        ws.Cell("A6").Value = "Project Name:";
        ws.Cell("B6").Value = projectName;
        ws.Cell("B6").Style.Font.Bold = true;
        ws.Cell("B6").Style.Font.FontSize = 14;

        ws.Cell("A7").Value = "Project Location:";
        // B7 blank

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

        // Configuration section (outside print area — row 20+)
        ws.Cell("A20").Value = "Pricing Workbook Path";
        ws.Cell("A20").Style.Font.Bold = true;
        ws.Cell("A20").Style.Font.FontColor = XLColor.Gray;
        ws.Cell("B20").Value = DefaultPricingPath;
        ws.Cell("B20").Style.Font.FontColor = XLColor.Gray;

        // Named range for pricing path
        wb.DefinedNames.Add("PricingWorkbookPath", ws.Range("B20:B20"));

        ws.Cell("A21").Value = "Notify Email";
        ws.Cell("A21").Style.Font.Bold = true;
        ws.Cell("A21").Style.Font.FontColor = XLColor.Gray;
        ws.Cell("B21").Style.Font.FontColor = XLColor.Gray;
        // B21 left blank — user enters recipient email address

        // Column widths
        ws.Column(1).Width = 22;
        ws.Column(2).Width = 50;

        // Print area excludes config section
        ws.PageSetup.PrintAreas.Add("A1:B17");
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

        // Style headers
        var headerRange = ws.Range(1, 1, 1, CsColChannel);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
        headerRange.Style.Font.FontColor = XLColor.White;

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
            row++;
        }

        // Auto-fit columns
        ws.Columns().AdjustToContents();
    }

    #endregion

    #region Worksheet Sheet

    private static void BuildWorksheetSheet(
        IXLWorkbook wb,
        List<CountsFixtureModel> fixtures,
        string countsSheetName,
        Dictionary<string, PricingEntry>? pricing,
        Dictionary<(string Type, string Catalog), WorksheetRowData>? existingRows)
    {
        var ws = wb.Worksheets.Add("Worksheet");

        // Headers
        ws.Cell(1, WsColType).Value = "Type";
        ws.Cell(1, WsColMfr).Value = "Mfr";
        ws.Cell(1, WsColCatalog).Value = "Catalog Number";
        ws.Cell(1, WsColQty).Value = "Qty";
        ws.Cell(1, WsColPrevQty).Value = "Prev Qty";
        ws.Cell(1, WsColDelta).Value = "Δ";
        ws.Cell(1, WsColCalc).Value = "Calc";
        ws.Cell(1, WsColPhase).Value = "Phase";
        ws.Cell(1, WsColDesc).Value = "Description";
        ws.Cell(1, WsColUnitCost).Value = "Unit Cost";
        ws.Cell(1, WsColMarkup).Value = "Markup %";
        ws.Cell(1, WsColTariff).Value = "Tariff %";
        ws.Cell(1, WsColAdder).Value = "Adder";

        // Style headers
        var headerRange = ws.Range(1, 1, 1, WsColPhase);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
        headerRange.Style.Font.FontColor = XLColor.White;

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
                ws.Cell(row, WsColMfr).Value = f.Manufacturer;
                ws.Cell(row, WsColCatalog).Value = catNum;

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
                    ws.Cell(row, WsColDesc).FormulaA1 = DependentFormula("G", firstRow);
                    ws.Cell(row, WsColCalc).FormulaA1 = $"IF(H{firstRow}=\"\",\"\",H{firstRow})";
                    ws.Cell(row, WsColUnitCost).FormulaA1 = DependentFormula("I", firstRow);
                    StyleAutoFilledCell(ws.Cell(row, WsColDesc));
                    StyleAutoFilledCell(ws.Cell(row, WsColCalc));
                    StyleAutoFilledCell(ws.Cell(row, WsColUnitCost));
                }
                else
                {
                    catalogFirstRow[catNum] = row;
                    if (pricing != null && pricing.TryGetValue(catNum, out var pe))
                    {
                        ws.Cell(row, WsColDesc).Value = pe.Description;
                        ws.Cell(row, WsColUnitCost).Value = pe.Cost;
                    }

                    // Mark canonical only when the catalog has siblings (otherwise every row would be bolded)
                    if (catalogCounts.GetValueOrDefault(catNum) > 1)
                        MarkCanonicalRow(ws, row, catNum);
                }

                // Tariff % (K) is per-Type canonical: only the first row of each Type holds a
                // literal; subsequent rows are blank and locked. Helper pipeline resolves the
                // Type's tariff % via XLOOKUP against the full K column (first match = canonical).
                if (!typeFirstRow.ContainsKey(f.TypeMark))
                    typeFirstRow[f.TypeMark] = row;

                // Active flag: initial export has no removed rows — always 1.
                ws.Cell(row, WsColActive).Value = 1;

                row++;
            }
        }

        int lastDataRow = row - 1;

        // Column widths
        ws.Column(WsColType).AdjustToContents();
        ws.Column(WsColMfr).AdjustToContents();
        ws.Column(WsColCatalog).AdjustToContents();
        ws.Column(WsColQty).Width = 8;
        ws.Column(WsColPrevQty).Width = 10;
        ws.Column(WsColDelta).Width = 6;
        ws.Column(WsColDesc).Width = 25;
        ws.Column(WsColCalc).Width = 10;
        ws.Column(WsColUnitCost).Width = 12;
        ws.Column(WsColMarkup).Width = 10;
        ws.Column(WsColTariff).Width = 10;
        ws.Column(WsColAdder).Width = 10;
        ws.Column(WsColPhase).Width = 10;

        // Format currency columns
        ws.Column(WsColUnitCost).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(WsColMarkup).Style.NumberFormat.Format = "0%";
        ws.Column(WsColTariff).Style.NumberFormat.Format = "0%";
        ws.Column(WsColAdder).Style.NumberFormat.Format = "$#,##0.00";

        // Delta: show +/- prefix, hide zero
        ws.Column(WsColDelta).Style.NumberFormat.Format = "+0;-0;;@";

        // Visual separator between TurboSuite (locked) and pricing (editable) columns
        ws.Column(WsColDelta).Style.Border.RightBorder = XLBorderStyleValues.Thick;

        // Hide gridlines so only the explicit type-group dividers read as separators
        ws.ShowGridLines = false;

        // Light gray divider at the last row of each Type group
        ApplyTypeGroupDividers(ws, 2, row - 1);

        // Helper pipeline (Z hidden flag already written per-row; AA-BB spill formulas here)
        WriteHelperPipeline(ws, lastDataRow);

        // Hide helper columns Z..BB
        for (int col = WsColActive; col <= WsColHelperLast; col++)
            ws.Column(col).Hide();

        // Sheet protection — lock TurboSuite columns (A-F), unlock user columns (G-M).
        // Exception: Tariff % (K) is only editable on each Type's canonical (first) row.
        var typeCanonicalRows = new HashSet<int>(typeFirstRow.Values);
        for (int r = 2; r < row; r++)
        {
            for (int col = WsColDesc; col <= WsColPhase; col++)
                ws.Cell(r, col).Style.Protection.SetLocked(false);
            if (!typeCanonicalRows.Contains(r))
                ws.Cell(r, WsColTariff).Style.Protection.SetLocked(true);
        }
        ws.Protect().AllowElement(XLSheetProtectionElements.FormatColumns);
    }

    private static void ApplyTypeGroupDividers(IXLWorksheet ws, int firstRow, int lastRow)
    {
        for (int r = firstRow; r < lastRow; r++)
        {
            string thisType = ws.Cell(r, WsColType).GetString();
            string nextType = ws.Cell(r + 1, WsColType).GetString();
            if (string.Equals(thisType, nextType, StringComparison.OrdinalIgnoreCase))
                continue;

            var rng = ws.Range(r, 1, r, WsColPhase);
            rng.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            rng.Style.Border.BottomBorderColor = XLColor.LightGray;
        }
    }

    private static string BuildQtyFormula(int row, string csRef)
    {
        // VLOOKUP indices: Col 9=Count, Col 10=LinearLength, Col 11=ReelLength, Col 12=ChannelLength
        return $"IF(H{row}=\"Reel\"," +
               $"CEILING(CEILING(VLOOKUP(A{row},{csRef}!A:L,10,FALSE)*1.05,1)/CEILING(VLOOKUP(A{row},{csRef}!A:L,11,FALSE),1),1)," +
               $"IF(H{row}=\"Channel\"," +
               $"CEILING(CEILING(VLOOKUP(A{row},{csRef}!A:L,10,FALSE)*1.05,1)/CEILING(VLOOKUP(A{row},{csRef}!A:L,12,FALSE),1),1)," +
               $"IF(H{row}=\"End Cap\"," +
               $"VLOOKUP(A{row},{csRef}!A:L,9,FALSE)," +
               $"IF(H{row}=\"Clip\"," +
               $"CEILING(CEILING(VLOOKUP(A{row},{csRef}!A:L,10,FALSE)*1.05,1)/1.75,1)," +
               $"VLOOKUP(A{row},{csRef}!A:L,9,FALSE)))))";
    }

    /// <summary>
    /// Writes the AA-BB helper column pipeline — one spill formula per final print-output column.
    /// Quote (AA-AG) + Phase 1/2/3 (AI-AN, AP-AU, AW-BB). Each column's formula:
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

        WriteSingleHelperPipeline(ws, lastDataRow, QuoteHelperCols,
            predicate: $"(Z2:Z{lastDataRow}=1)", includeDelta: true);
        WriteSingleHelperPipeline(ws, lastDataRow, Phase1HelperCols,
            predicate: $"(Z2:Z{lastDataRow}=1)*(M2:M{lastDataRow}=1)", includeDelta: false);
        WriteSingleHelperPipeline(ws, lastDataRow, Phase2HelperCols,
            predicate: $"(Z2:Z{lastDataRow}=1)*(M2:M{lastDataRow}=2)", includeDelta: false);
        WriteSingleHelperPipeline(ws, lastDataRow, Phase3HelperCols,
            predicate: $"(Z2:Z{lastDataRow}=1)*(M2:M{lastDataRow}=3)", includeDelta: false);
    }

    private static void WriteSingleHelperPipeline(
        IXLWorksheet ws, int lastDataRow, string[] cols, string predicate, bool includeDelta)
    {
        // Per-row array expressions (unfiltered; FILTER applied inside Gap).
        string Col(string c) => $"{c}2:{c}{lastDataRow}";
        string sellEa = $"IFERROR(({Col("I")}*(1+{Col("J")}))+{Col("L")},0)";
        string sellExt = $"({sellEa})*{Col("D")}";
        // Tariff base = Sell Ext. (includes Adder). Prior version omitted L, underpricing tariffs.
        string tariffBasePerRow = sellExt;
        string catalogCombined =
            $"{Col("C")}&IF(({Col("G")}<>0)*({Col("G")}<>\"\"),\" ~ \"&{Col("G")},\"\")";

        // Gap LAMBDA: emits gap rows at type-group boundaries and a "Tariff" row at each
        // group's end (when tariff% != 0). Inline LAMBDA — defined-name LAMBDAs called from
        // spill cells trip Excel's load-time parser.
        // Per-row type tariff %: XLOOKUP against the full A/K ranges returns the first match,
        // which is the Type's canonical row (where the literal K value lives). Non-canonical
        // rows have K blank, so only the canonical is found. Wrapped in IFERROR to coerce a
        // blank canonical K to 0 so arithmetic downstream stays numeric.
        string typeKPerRow = $"IFERROR(_xlfn.XLOOKUP({Col("A")},{Col("A")},{Col("K")}),0)";
        string typesArg = $"_xlfn._xlws.FILTER({Col("A")},{predicate})";
        string pctsArg = $"_xlfn._xlws.FILTER({typeKPerRow},{predicate})";
        string baseArg = $"_xlfn._xlws.FILTER({tariffBasePerRow},{predicate})";
        string Gap(string valsExpr, string tariffContentExpr)
        {
            string valsArg = $"_xlfn._xlws.FILTER({valsExpr},{predicate})";
            return "_xlfn.LAMBDA(_xlpm.types,_xlpm.vals,_xlpm.pcts,_xlpm.base,"
                 +   "IF(ROWS(_xlpm.vals)<=1,_xlpm.vals,"
                 +     "_xlfn.LET("
                 +       "_xlpm.prev,_xlfn.VSTACK(INDEX(_xlpm.types,1),_xlfn.DROP(_xlpm.types,-1)),"
                 +       "_xlpm.nxt,_xlfn.VSTACK(_xlfn.DROP(_xlpm.types,1),\"\"),"
                 +       "_xlpm.gapCol,IF(_xlpm.types<>_xlpm.prev,\"\",_xlfn.NA()),"
                 +       "_xlpm.isLast,_xlpm.types<>_xlpm.nxt,"
                 +       "_xlpm.totals,_xlfn.BYROW(_xlpm.types,_xlfn.LAMBDA(_xlpm.tv,SUMPRODUCT((_xlpm.types=_xlpm.tv)*_xlpm.base))),"
                 +       $"_xlpm.tariffCol,IF(_xlpm.isLast*(_xlpm.pcts<>0),{tariffContentExpr},_xlfn.NA()),"
                 +       "_xlfn.TOCOL(_xlfn.HSTACK(_xlpm.gapCol,_xlpm.vals,_xlpm.tariffCol),2)"
                 +     ")"
                 +   ")"
                 + $")({typesArg},{valsArg},{pctsArg},{baseArg})";
        }

        // Subtotal = Σ(filtered line items) + Σ(filtered per-row tariff allocation).
        // K is only populated on each Type's canonical row, so we use the per-row XLOOKUP
        // resolver (typeKPerRow) to broadcast the type's tariff % onto every row in the group.
        string subtotal =
            $"SUMPRODUCT(({predicate})*{sellEa}*{Col("D")})"
            + $"+SUMPRODUCT(({predicate})*{tariffBasePerRow}*{typeKPerRow})";

        int i = 0;
        // Type — blank tariff row
        ws.Cell($"{cols[i++]}2").FormulaA1 = $"IFERROR({Gap(Col("A"), "\"\"")},\"\")";
        // Mfr — blank tariff row
        ws.Cell($"{cols[i++]}2").FormulaA1 = $"IFERROR({Gap(Col("B"), "\"\"")},\"\")";
        // Catalog~Desc — "Tariff" label on tariff row
        ws.Cell($"{cols[i++]}2").FormulaA1 = $"IFERROR({Gap(catalogCombined, "\"Tariff\"")},\"\")";
        // Qty — blank tariff row
        ws.Cell($"{cols[i++]}2").FormulaA1 = $"IFERROR({Gap(Col("D"), "\"\"")},\"\")";
        // Delta (Quote only) — blank tariff row
        if (includeDelta)
            ws.Cell($"{cols[i++]}2").FormulaA1 = $"IFERROR({Gap(Col("F"), "\"\"")},\"\")";
        // Sell Ea. + footer labels
        ws.Cell($"{cols[i++]}2").FormulaA1 =
            $"_xlfn.VSTACK(IFERROR({Gap(sellEa, "\"\"")},\"\"),\"\",\"Subtotal:\",\"Freight:\",\"Grand Total:\")";
        // Sell Ext. + footer values (tariff row carries per-type tariff amount)
        ws.Cell($"{cols[i++]}2").FormulaA1 =
            $"_xlfn.VSTACK(IFERROR({Gap(sellExt, "_xlpm.totals*_xlpm.pcts")},\"\"),\"\",{subtotal},0,{subtotal}+0)";
    }

    #endregion

    #region Print Sheets (Quote, Phase 1/2/3)

    /// <summary>
    /// Builds the Quote print sheet as a thin consumer of Worksheet helper columns AA-AG.
    /// 7 single-cell ANCHORARRAY formulas at row 7. All pipeline logic (filter, gap rows,
    /// tariff rows, subtotal/freight/grand-total footer) lives on Worksheet — this sheet is
    /// print formatting only.
    /// </summary>
    private static void BuildQuoteSheet(IXLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Quote");
        if (!wb.Worksheets.TryGetWorksheet("Worksheet", out var wsSheet))
            return;

        WritePrintSheetTitle(ws, 7, "\"PRODUCT PRICING \"&Cover!B11");

        int headerRow = 6;
        string[] headers = { "Type", "Mfr", "Catalog Number", "Qty", "Δ", "Sell Ea.", "Sell Ext." };
        WritePrintSheetHeaders(ws, headerRow, headers);

        // Spill row — one ANCHORARRAY per column pointing at Worksheet!AAn..AGn.
        int spillRow = headerRow + 1;
        for (int i = 0; i < QuoteHelperCols.Length; i++)
            ws.Cell(spillRow, i + 1).FormulaA1 = $"_xlfn.ANCHORARRAY(Worksheet!{QuoteHelperCols[i]}2)";

        // Currency + delta formats
        ws.Column(6).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(7).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(5).Style.NumberFormat.Format = "+0;-0;;@";

        // Column widths — pull from Worksheet (ANCHORARRAY cells can't auto-size).
        ws.Column(1).Width = wsSheet.Column(WsColType).Width;
        ws.Column(2).Width = wsSheet.Column(WsColMfr).Width;
        ws.Column(3).Width = ComputeCombinedCatalogWidth(wsSheet);
        ws.Column(4).Width = 8;
        ws.Column(5).Width = 6;
        ws.Column(6).Width = 12;
        ws.Column(7).Width = 14;

        // Print setup
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.SetRowsToRepeatAtTop(1, headerRow);
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

        WritePrintSheetTitle(ws, 6, $"\"PHASE {phase} PRODUCT PRICING \"&Cover!B11");

        int headerRow = 6;
        string[] headers = { "Type", "Mfr", "Catalog Number", "Qty", "Sell Ea.", "Sell Ext." };
        WritePrintSheetHeaders(ws, headerRow, headers);

        int spillRow = headerRow + 1;
        for (int i = 0; i < cols.Length; i++)
            ws.Cell(spillRow, i + 1).FormulaA1 = $"_xlfn.ANCHORARRAY(Worksheet!{cols[i]}2)";

        // Currency formats
        ws.Column(5).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(6).Style.NumberFormat.Format = "$#,##0.00";

        // Column widths
        ws.Column(1).Width = wsSheet.Column(WsColType).Width;
        ws.Column(2).Width = wsSheet.Column(WsColMfr).Width;
        ws.Column(3).Width = ComputeCombinedCatalogWidth(wsSheet);
        ws.Column(4).Width = 8;
        ws.Column(5).Width = 12;
        ws.Column(6).Width = 14;

        // Print setup
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.SetRowsToRepeatAtTop(1, headerRow);
    }

    /// <summary>Writes the 4-row merged title block shared by Quote and Phase sheets.</summary>
    private static void WritePrintSheetTitle(IXLWorksheet ws, int mergeColCount, string subtitleFormula)
    {
        string lastCol = XLHelper.GetColumnLetterFromNumber(mergeColCount);

        ws.Range($"A1:{lastCol}1").Merge();
        ws.Cell("A1").FormulaA1 = "Cover!B6";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 16;
        ws.Cell("A1").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Range($"A2:{lastCol}2").Merge();
        ws.Cell("A2").FormulaA1 = subtitleFormula;
        ws.Cell("A2").Style.Font.Bold = true;
        ws.Cell("A2").Style.Font.FontSize = 12;
        ws.Cell("A2").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Range($"A3:{lastCol}3").Merge();
        ws.Cell("A3").Value = "ANY SUBSTITUTIONS MUST BE APPROVED BY CDLTG";
        ws.Cell("A3").Style.Font.FontColor = XLColor.Red;
        ws.Cell("A3").Style.Font.Bold = true;
        ws.Cell("A3").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Range($"A4:{lastCol}4").Merge();
        ws.Cell("A4").Value = "ALL PRICING BELOW IS VALID FOR 5 BUSINESS DAYS";
        ws.Cell("A4").Style.Font.FontColor = XLColor.Red;
        ws.Cell("A4").Style.Font.Bold = true;
        ws.Cell("A4").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    private static void WritePrintSheetHeaders(IXLWorksheet ws, int headerRow, string[] headers)
    {
        for (int i = 0; i < headers.Length; i++)
            ws.Cell(headerRow, i + 1).Value = headers[i];
        var range = ws.Range(headerRow, 1, headerRow, headers.Length);
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
        range.Style.Font.FontColor = XLColor.White;
    }

    #endregion

    #region Changes Sheet

    private static void BuildChangesSheet(IXLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Changes");

        ws.Cell(1, 1).Value = "Date";
        ws.Cell(1, 2).Value = "Type";
        ws.Cell(1, 3).Value = "Change";
        ws.Cell(1, 4).Value = "Old Value";
        ws.Cell(1, 5).Value = "New Value";

        var headerRange = ws.Range(1, 1, 1, 5);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
        headerRange.Style.Font.FontColor = XLColor.White;

        ws.Column(1).Width = 14;
        ws.Column(2).Width = 12;
        ws.Column(3).Width = 18;
        ws.Column(4).Width = 30;
        ws.Column(5).Width = 30;
    }

    #endregion

    #region Update Logic

    private static void UpdateWorksheetSheet(
        IXLWorkbook wb,
        List<CountsFixtureModel> fixtures,
        string countsSheetName,
        Dictionary<string, PricingEntry>? pricing,
        List<WorksheetRowData> existingRows,
        Dictionary<string, CountsFixtureModel>? prevData)
    {
        if (!wb.Worksheets.TryGetWorksheet("Worksheet", out var ws))
        {
            // No existing Worksheet — build fresh
            BuildWorksheetSheet(wb, fixtures, countsSheetName, pricing, null);
            return;
        }

        string csRef = $"'{countsSheetName}'";

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
        foreach (var f in fixtures)
        {
            newTypeMarks.Add(f.TypeMark);
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
            var dataRange = ws.Range(2, 1, lastRow, WsColAdder);
            dataRange.Style.Fill.BackgroundColor = XLColor.NoColor;
            dataRange.Style.Font.Strikethrough = false;
        }

        // Step 1: Delete rows that were already marked as removed (red strikethrough)
        // We detect these by checking if the row's (Type, Catalog) is not in new data
        // AND was already not in prev data (meaning it was marked red last time)
        // For simplicity on first implementation: just delete rows not in new data
        // that have strikethrough font
        for (int r = lastRow; r >= 2; r--)
        {
            string rowType = ws.Cell(r, WsColType).GetString();
            string rowCat = ws.Cell(r, WsColCatalog).GetString();
            var key = (rowType.ToUpperInvariant(), rowCat.ToUpperInvariant());

            if (!newKeys.Contains(key) && ws.Cell(r, WsColType).Style.Font.Strikethrough)
            {
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

        // Step 2: Clear existing data rows and rebuild
        lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        if (lastRow > 1)
        {
            for (int r = lastRow; r >= 2; r--)
                ws.Row(r).Delete();
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
            ws.Cell(row, WsColMfr).Value = mfr;
            ws.Cell(row, WsColCatalog).Value = catalog;
            ws.Cell(row, WsColQty).FormulaA1 = BuildQtyFormula(row, csRef);
            ws.Cell(row, WsColDelta).FormulaA1 = $"IF(E{row}=\"\",\"\",D{row}-E{row})";
            ws.Cell(row, WsColCalc).GetDataValidation().List("\"Reel,Channel,End Cap,Clip\"", true);

            // Prev Qty — use previous Worksheet's computed Qty (reflects Calc adjustments)
            // Fall back to raw fixture Count if no existing row (new type on this pass)
            if (existing?.PrevQty.HasValue == true)
                ws.Cell(row, WsColPrevQty).Value = existing.PrevQty.Value;
            else if (prevData != null && prevData.TryGetValue(type, out var prevFixture))
                ws.Cell(row, WsColPrevQty).Value = prevFixture.Count;

            int typeCanonical = typeCanonicalSheetRow[type];

            if (existing != null)
            {
                // Preserve Phase (Calc is handled via shared pricing logic below)
                if (existing.Phase.HasValue)
                    ws.Cell(row, WsColPhase).Value = existing.Phase.Value;

                // Catalog-canonical fields
                WritePricingCells(ws, row, canonicalRow, catalog, pricing,
                    existing.Description, existing.Calc, existing.UnitCost, existing.Markup, existing.Adder,
                    existing.DescIsFormula, existing.CalcIsFormula, existing.CostIsFormula,
                    isNewRow: false);

                // Type-canonical field (Tariff): preserve existing literal on canonical row.
                WriteTariffCell(ws, row, typeCanonical, existing.Tariff, isNewRow: false);
            }
            else
            {
                // New row — no existing data
                WritePricingCells(ws, row, canonicalRow, catalog, pricing,
                    null, null, null, null, null, false, false, false, isNewRow: true);

                WriteTariffCell(ws, row, typeCanonical, null, isNewRow: true);

                if (isNewType)
                {
                    // Brand-new type: Prev Qty = 0 so delta shows +qty; green across Revit-side cells.
                    ws.Cell(row, WsColPrevQty).Value = 0;
                    for (int col = 1; col <= WsColDelta; col++)
                        ws.Cell(row, col).Style.Fill.BackgroundColor = GreenFill;
                }
                else
                {
                    // New catalog under existing type: yellow through Qty only (Prev Qty/Delta stay clean).
                    for (int col = 1; col <= WsColQty; col++)
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
            ws.Cell(row, WsColMfr).Value = mfr;
            ws.Cell(row, WsColCatalog).Value = catalog;

            if (existing != null)
            {
                ws.Cell(row, WsColCalc).Value = existing.Calc;
                // Phase intentionally left blank — removed rows must not appear in Phase sheet FILTER results
                ws.Cell(row, WsColDesc).Value = existing.Description ?? "";
                if (existing.UnitCost.HasValue) ws.Cell(row, WsColUnitCost).Value = existing.UnitCost.Value;
                if (existing.Markup.HasValue) ws.Cell(row, WsColMarkup).Value = existing.Markup.Value;
                if (existing.Tariff.HasValue) ws.Cell(row, WsColTariff).Value = existing.Tariff.Value;
                if (existing.Adder.HasValue) ws.Cell(row, WsColAdder).Value = existing.Adder.Value;
            }

            // Red fill + strikethrough — extends through Phase so the whole row reads as removed
            for (int col = 1; col <= WsColPhase; col++)
            {
                ws.Cell(row, col).Style.Fill.BackgroundColor = RedFill;
                ws.Cell(row, col).Style.Font.Strikethrough = true;
            }

            // Active flag: removed — excluded from spill filters
            ws.Cell(row, WsColActive).Value = 0;

            row++;
        }

        int lastDataRow = row - 1;

        // Visual separator between TurboSuite (locked) and pricing (editable) columns
        ws.Column(WsColDelta).Style.Border.RightBorder = XLBorderStyleValues.Thick;

        // Hide gridlines so only the explicit type-group dividers read as separators
        ws.ShowGridLines = false;

        // Light gray divider at the last row of each Type group
        ApplyTypeGroupDividers(ws, 2, lastDataRow);

        // Helper pipeline (AA-BB) — re-emit with updated lastDataRow bounds
        WriteHelperPipeline(ws, lastDataRow);

        // Hide helper columns Z..BB (no-op on re-runs)
        for (int col = WsColActive; col <= WsColHelperLast; col++)
            ws.Column(col).Hide();

        // Re-apply protection. Tariff % (K) is only editable on each Type's canonical (first) row.
        var typeCanonicalRowsSet = new HashSet<int>(typeCanonicalSheetRow.Values);
        for (int r = 2; r < row; r++)
        {
            for (int col = WsColDesc; col <= WsColPhase; col++)
                ws.Cell(r, col).Style.Protection.SetLocked(false);
            if (!typeCanonicalRowsSet.Contains(r))
                ws.Cell(r, WsColTariff).Style.Protection.SetLocked(true);
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
        }
        // Excel column-width units are sized to the digit "0"; letters average wider, so
        // raw char count undercounts. Multiply by 1.2 + small pad to match proportional text.
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
        IXLWorksheet ws, int row, int canonicalRow, string catalog,
        Dictionary<string, PricingEntry>? pricing,
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
            // Canonical row: Desc / Calc / Unit Cost get literal values.
            if (!isNewRow)
            {
                if (existingDesc != null) ws.Cell(row, WsColDesc).Value = existingDesc;
                if (!string.IsNullOrEmpty(existingCalc)) ws.Cell(row, WsColCalc).Value = existingCalc;
                if (existingCost.HasValue) ws.Cell(row, WsColUnitCost).Value = existingCost.Value;
            }
            else if (pricing != null && pricing.TryGetValue(catalog, out var pe))
            {
                ws.Cell(row, WsColDesc).Value = pe.Description;
                ws.Cell(row, WsColUnitCost).Value = pe.Cost;
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
            ws.Cell(row, WsColDesc).FormulaA1 = DependentFormula("G", canonicalRow);
            StyleAutoFilledCell(ws.Cell(row, WsColDesc));
        }

        if (!isNewRow && !calcIsFormula && !string.IsNullOrEmpty(existingCalc))
        {
            ws.Cell(row, WsColCalc).Value = existingCalc;
        }
        else
        {
            ws.Cell(row, WsColCalc).FormulaA1 = $"IF(H{canonicalRow}=\"\",\"\",H{canonicalRow})";
            StyleAutoFilledCell(ws.Cell(row, WsColCalc));
        }

        if (!isNewRow && !costIsFormula && existingCost.HasValue)
        {
            ws.Cell(row, WsColUnitCost).Value = existingCost.Value;
        }
        else
        {
            ws.Cell(row, WsColUnitCost).FormulaA1 = DependentFormula("I", canonicalRow);
            StyleAutoFilledCell(ws.Cell(row, WsColUnitCost));
        }
    }

    /// <summary>
    /// Writes the Tariff cell. Tariff is a per-Type value — only the first row of each Type
    /// holds a literal; non-canonical rows stay blank (and locked via the protection pass).
    /// Helper pipeline resolves per-row tariff % via XLOOKUP into the full K column.
    /// </summary>
    private static void WriteTariffCell(
        IXLWorksheet ws, int row, int typeCanonicalRow,
        double? existingTariff, bool isNewRow)
    {
        if (row != typeCanonicalRow) return;
        if (!isNewRow && existingTariff.HasValue)
            ws.Cell(row, WsColTariff).Value = existingTariff.Value;
    }

    private static double? ReadNumericCell(IXLCell cell)
    {
        if (cell.HasFormula) return null;
        if (cell.IsEmpty()) return null;
        return cell.TryGetValue<double>(out double v) ? v : null;
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
        int row = (ws.LastRowUsed()?.RowNumber() ?? 1) + 1;

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
    }

    private static void WriteChangeRow(IXLWorksheet ws, ref int row, string date, string type, string change, string oldVal, string newVal)
    {
        ws.Cell(row, 1).Value = date;
        ws.Cell(row, 2).Value = type;
        ws.Cell(row, 3).Value = change;
        ws.Cell(row, 4).Value = oldVal;
        ws.Cell(row, 5).Value = newVal;
        row++;
    }

    #endregion

    #region Helpers

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

    private static string ReadPricingPathFromCover(IXLWorkbook wb)
    {
        if (!wb.Worksheets.TryGetWorksheet("Cover", out var ws))
            return DefaultPricingPath;

        return ws.Cell("B20").GetString();
    }

    /// <summary>
    /// Reads the notify email address from the Cover sheet config area of a saved workbook.
    /// </summary>
    public static string ReadNotifyEmailFromCover(string workbookPath)
    {
        try
        {
            using var wb = new XLWorkbook(workbookPath);
            if (!wb.Worksheets.TryGetWorksheet("Cover", out var ws))
                return string.Empty;
            return ws.Cell("B21").GetString();
        }
        catch
        {
            return string.Empty;
        }
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
                Description = ws.Cell(r, WsColDesc).HasFormula ? null : ws.Cell(r, WsColDesc).GetString(),
                UnitCost = ReadNumericCell(ws.Cell(r, WsColUnitCost)),
                Markup = ReadNumericCell(ws.Cell(r, WsColMarkup)),
                Adder = ReadNumericCell(ws.Cell(r, WsColAdder)),
                Tariff = ReadNumericCell(ws.Cell(r, WsColTariff)),
                DescIsFormula = ws.Cell(r, WsColDesc).HasFormula,
                CalcIsFormula = ws.Cell(r, WsColCalc).HasFormula,
                CostIsFormula = ws.Cell(r, WsColUnitCost).HasFormula,
                IsStrikethrough = ws.Cell(r, WsColType).Style.Font.Strikethrough,
            });
        }

        return result;
    }

    private static Dictionary<string, PricingEntry>? ReadPricingWorkbook(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheets.TryGetWorksheet("Pricing", out var pricingSheet)
                ? pricingSheet
                : wb.Worksheets.First();

            var result = new Dictionary<string, PricingEntry>(StringComparer.OrdinalIgnoreCase);
            int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

            for (int r = 2; r <= lastRow; r++)
            {
                string catalogNumber = ws.Cell(r, 1).GetString();
                if (string.IsNullOrWhiteSpace(catalogNumber)) continue;

                result.TryAdd(catalogNumber, new PricingEntry
                {
                    Description = ws.Cell(r, 2).GetString(),
                    Cost = (decimal)ws.Cell(r, 3).GetDouble(),
                });
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Internal Types

    private class PricingEntry
    {
        public string Description { get; init; } = string.Empty;
        public decimal Cost { get; init; }
    }

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
}
