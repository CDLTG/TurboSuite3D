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

        // Patch dynamic array metadata for Phase sheets (ClosedXML doesn't write XLDAPR)
        var phaseNames = Enumerable.Range(1, 3).Select(p => $"Phase {p}").ToList();
        PatchDynamicArrayMetadata(outputPath, phaseNames);
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

        // Rebuild Quote and Phase sheets
        if (wb.Worksheets.TryGetWorksheet("Quote", out var oldQuote))
            wb.Worksheets.Delete("Quote");
        BuildQuoteSheet(wb);

        for (int p = 1; p <= 3; p++)
        {
            if (wb.Worksheets.TryGetWorksheet($"Phase {p}", out _))
                wb.Worksheets.Delete($"Phase {p}");
            BuildPhaseQuoteSheet(wb, p);
        }

        // Append changes
        if (prevData != null)
            AppendChanges(wb, fixtures, prevData);

        // Restore canonical sheet order (Quote/Phase were deleted and re-added at the end)
        ReorderSheets(wb);

        wb.Save();

        // Patch dynamic array metadata for Phase sheets (ClosedXML doesn't write XLDAPR)
        var phaseNames = Enumerable.Range(1, 3).Select(p => $"Phase {p}").ToList();
        PatchDynamicArrayMetadata(existingPath, phaseNames);
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
        var headerRange = ws.Range(1, 1, 1, WsColAdder);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
        headerRange.Style.Font.FontColor = XLColor.White;

        string csRef = $"'{countsSheetName}'";

        // Track first occurrence of each catalog number for shared pricing
        var catalogFirstRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Pre-pass: count how many times each catalog appears — needed to decide whether to mark canonical
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

                // Description + Unit Cost (pricing lookup or shared reference)
                if (catalogFirstRow.TryGetValue(catNum, out int firstRow))
                {
                    // Subsequent occurrence — reference first, styled as auto-filled
                    ws.Cell(row, WsColDesc).FormulaA1 = $"G{firstRow}";
                    ws.Cell(row, WsColUnitCost).FormulaA1 = $"I{firstRow}";
                    ws.Cell(row, WsColMarkup).FormulaA1 = $"J{firstRow}";
                    ws.Cell(row, WsColTariff).FormulaA1 = $"K{firstRow}";
                    ws.Cell(row, WsColAdder).FormulaA1 = $"L{firstRow}";
                    StyleAutoFilledCell(ws.Cell(row, WsColDesc));
                    StyleAutoFilledCell(ws.Cell(row, WsColUnitCost));
                    StyleAutoFilledCell(ws.Cell(row, WsColMarkup));
                    StyleAutoFilledCell(ws.Cell(row, WsColTariff));
                    StyleAutoFilledCell(ws.Cell(row, WsColAdder));
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

                row++;
            }
        }

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

        // Sheet protection — lock TurboSuite columns (A-F), unlock user columns (G-M)
        for (int r = 2; r < row; r++)
        {
            for (int col = WsColDesc; col <= WsColPhase; col++)
                ws.Cell(r, col).Style.Protection.SetLocked(false);
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

    #endregion

    #region Quote Sheet

    private static void BuildQuoteSheet(IXLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Quote");

        if (!wb.Worksheets.TryGetWorksheet("Worksheet", out var wsSheet))
            return;

        // Header area — merge across all columns and center
        ws.Range("A1:H1").Merge();
        ws.Cell("A1").FormulaA1 = "Cover!B6";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 16;
        ws.Cell("A1").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Range("A2:H2").Merge();
        ws.Cell("A2").FormulaA1 = "\"PRODUCT PRICING \"&Cover!B11";
        ws.Cell("A2").Style.Font.Bold = true;
        ws.Cell("A2").Style.Font.FontSize = 12;
        ws.Cell("A2").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Range("A3:H3").Merge();
        ws.Cell("A3").Value = "ANY SUBSTITUTIONS MUST BE APPROVED BY CDLTG";
        ws.Cell("A3").Style.Font.FontColor = XLColor.Red;
        ws.Cell("A3").Style.Font.Bold = true;
        ws.Cell("A3").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Range("A4:H4").Merge();
        ws.Cell("A4").Value = "ALL PRICING BELOW IS VALID FOR 5 BUSINESS DAYS";
        ws.Cell("A4").Style.Font.FontColor = XLColor.Red;
        ws.Cell("A4").Style.Font.Bold = true;
        ws.Cell("A4").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        // Column headers at row 6
        int headerRow = 6;
        ws.Cell(headerRow, 1).Value = "Type";
        ws.Cell(headerRow, 2).Value = "Mfr";
        ws.Cell(headerRow, 3).Value = "Catalog Number";
        ws.Cell(headerRow, 4).Value = "Description";
        ws.Cell(headerRow, 5).Value = "Qty";
        ws.Cell(headerRow, 6).Value = "Δ";
        ws.Cell(headerRow, 7).Value = "Sell Ea.";
        ws.Cell(headerRow, 8).Value = "Sell Ext.";

        var qHeaderRange = ws.Range(headerRow, 1, headerRow, 8);
        qHeaderRange.Style.Font.Bold = true;
        qHeaderRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
        qHeaderRange.Style.Font.FontColor = XLColor.White;

        // Find data range in Worksheet
        int lastWsRow = wsSheet.LastRowUsed()?.RowNumber() ?? 1;
        if (lastWsRow <= 1) return; // no data

        int qRow = headerRow + 1;
        string? prevQuoteType = null;
        for (int wsRow = 2; wsRow <= lastWsRow; wsRow++)
        {
            // Skip rows marked as removed (red strikethrough in Worksheet)
            if (wsSheet.Cell(wsRow, WsColType).Style.Font.Strikethrough)
                continue;

            string wsType = wsSheet.Cell(wsRow, WsColType).GetString();

            // Blank separator row between Type Mark groups
            if (prevQuoteType != null && !string.Equals(prevQuoteType, wsType, StringComparison.OrdinalIgnoreCase))
                qRow++;
            prevQuoteType = wsType;

            ws.Cell(qRow, 1).FormulaA1 = $"Worksheet!A{wsRow}";   // Type
            ws.Cell(qRow, 2).FormulaA1 = $"Worksheet!B{wsRow}";   // Mfr
            ws.Cell(qRow, 3).FormulaA1 = $"Worksheet!C{wsRow}";   // Catalog Number
            ws.Cell(qRow, 4).FormulaA1 = $"IF(Worksheet!G{wsRow}=0,\"\",Worksheet!G{wsRow})";   // Description
            ws.Cell(qRow, 5).FormulaA1 = $"Worksheet!D{wsRow}";   // Qty
            ws.Cell(qRow, 6).FormulaA1 = $"Worksheet!F{wsRow}";   // Delta
            ws.Cell(qRow, 7).FormulaA1 = $"IFERROR((Worksheet!I{wsRow}*(1+Worksheet!J{wsRow})*(1+Worksheet!K{wsRow}))+Worksheet!L{wsRow},0)"; // Sell Ea.
            ws.Cell(qRow, 8).FormulaA1 = $"G{qRow}*E{qRow}";     // Sell Ext. = Sell Ea. * Qty

            qRow++;
        }

        int dataStart = headerRow + 1;
        int dataEnd = qRow - 1;

        // Subtotals
        if (dataEnd >= dataStart)
        {
            int subtotalRow = dataEnd + 2;

            // Subtotal — sum of visible Sell Ext. rows
            ws.Cell(subtotalRow, 7).Value = "Subtotal:";
            ws.Cell(subtotalRow, 7).Style.Font.Bold = true;
            ws.Cell(subtotalRow, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(subtotalRow, 8).FormulaA1 = $"SUBTOTAL(109,H{dataStart}:H{dataEnd})";

            // Freight — user-entered value
            ws.Cell(subtotalRow + 1, 7).Value = "Freight:";
            ws.Cell(subtotalRow + 1, 7).Style.Font.Bold = true;
            ws.Cell(subtotalRow + 1, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            // Leave H cell blank for user to enter freight cost

            // Grand Total = Subtotal + Freight
            ws.Cell(subtotalRow + 2, 7).Value = "Grand Total:";
            ws.Cell(subtotalRow + 2, 7).Style.Font.Bold = true;
            ws.Cell(subtotalRow + 2, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(subtotalRow + 2, 8).FormulaA1 = $"H{subtotalRow}+H{subtotalRow + 1}";

            // Format totals cells
            for (int sr = subtotalRow; sr <= subtotalRow + 2; sr++)
            {
                ws.Cell(sr, 8).Style.Font.Bold = true;
                ws.Cell(sr, 8).Style.NumberFormat.Format = "$#,##0.00";
            }
        }

        // Format currency columns
        ws.Column(7).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(8).Style.NumberFormat.Format = "$#,##0.00";

        // Delta: show +/- prefix, hide zero
        ws.Column(6).Style.NumberFormat.Format = "+0;-0;;@";

        // Column widths — pull from Worksheet (Quote cells are formulas, can't auto-size)
        ws.Column(1).Width = wsSheet.Column(WsColType).Width;
        ws.Column(2).Width = wsSheet.Column(WsColMfr).Width;
        ws.Column(3).Width = wsSheet.Column(WsColCatalog).Width;
        ws.Column(4).Width = wsSheet.Column(WsColDesc).Width;
        ws.Column(5).Width = 8;
        ws.Column(6).Width = 6;
        ws.Column(7).Width = 12;
        ws.Column(8).Width = 14;

        // Auto-filter on header row
        if (dataEnd >= dataStart)
            ws.Range(headerRow, 1, dataEnd, 8).SetAutoFilter();

        // Print setup
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0); // fit width, unlimited height
        ws.PageSetup.SetRowsToRepeatAtTop(1, headerRow); // repeat header rows
    }

    private static void BuildPhaseQuoteSheet(IXLWorkbook wb, int phase)
    {
        string sheetName = $"Phase {phase}";
        var ws = wb.Worksheets.Add(sheetName);

        if (!wb.Worksheets.TryGetWorksheet("Worksheet", out var wsSheet))
            return;

        // Header area — 7 columns (no Δ or Phase)
        ws.Range("A1:G1").Merge();
        ws.Cell("A1").FormulaA1 = "Cover!B6";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 16;
        ws.Cell("A1").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Range("A2:G2").Merge();
        ws.Cell("A2").FormulaA1 = $"\"PHASE {phase} PRODUCT PRICING \"&Cover!B11";
        ws.Cell("A2").Style.Font.Bold = true;
        ws.Cell("A2").Style.Font.FontSize = 12;
        ws.Cell("A2").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Range("A3:G3").Merge();
        ws.Cell("A3").Value = "ANY SUBSTITUTIONS MUST BE APPROVED BY CDLTG";
        ws.Cell("A3").Style.Font.FontColor = XLColor.Red;
        ws.Cell("A3").Style.Font.Bold = true;
        ws.Cell("A3").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Range("A4:G4").Merge();
        ws.Cell("A4").Value = "ALL PRICING BELOW IS VALID FOR 5 BUSINESS DAYS";
        ws.Cell("A4").Style.Font.FontColor = XLColor.Red;
        ws.Cell("A4").Style.Font.Bold = true;
        ws.Cell("A4").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        // Column headers at row 6
        int headerRow = 6;
        ws.Cell(headerRow, 1).Value = "Type";
        ws.Cell(headerRow, 2).Value = "Mfr";
        ws.Cell(headerRow, 3).Value = "Catalog Number";
        ws.Cell(headerRow, 4).Value = "Description";
        ws.Cell(headerRow, 5).Value = "Qty";
        ws.Cell(headerRow, 6).Value = "Sell Ea.";
        ws.Cell(headerRow, 7).Value = "Sell Ext.";

        var qHeaderRange = ws.Range(headerRow, 1, headerRow, 7);
        qHeaderRange.Style.Font.Bold = true;
        qHeaderRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
        qHeaderRange.Style.Font.FontColor = XLColor.White;

        int lastWsRow = wsSheet.LastRowUsed()?.RowNumber() ?? 1;
        if (lastWsRow <= 1) return;

        // Worksheet range helpers
        string ws_(string col) => $"Worksheet!${col}$2:${col}${lastWsRow}";
        string cond = $"{ws_("M")}={phase}";

        // Sell Ea. computation from Worksheet pricing columns
        string sellEa = $"IFERROR(({ws_("I")}*(1+{ws_("J")})*(1+{ws_("K")}))+{ws_("L")},0)";

        // Sell Ext. = Sell Ea. * Qty
        string sellExt = $"({sellEa})*{ws_("D")}";

        // Subtotal via SUMPRODUCT (always computable, even when FILTER is empty)
        string subtotal = $"SUMPRODUCT(({cond})*{sellEa}*{ws_("D")})";

        // Inlined LAMBDA (immediately invoked) that emits a blank row at each type-group
        // boundary, producing type-grouped output for Phase sheets. Inline rather than
        // behind a defined name — defined-name LAMBDA calls from spill cells trip Excel's
        // load-time parser.
        //
        // Approach: build a "gap marker" column where each row holds "" if it starts a
        // new type group (else NA()), HSTACK with the values column, then TOCOL with
        // ignore-errors flattens row-major, dropping NA() and keeping "" as gap rows.
        // Avoids REDUCE's growing accumulator which produced spurious mid-group gaps.
        //
        // Stored form: _xlfn. for 365 functions, _xlpm. for LAMBDA/LET parameter refs.
        string typesArg = $"_xlfn._xlws.FILTER({ws_("A")},{cond})";
        string Gap(string valsExpr)
        {
            string valsArg = $"_xlfn._xlws.FILTER({valsExpr},{cond})";
            return "_xlfn.LAMBDA(_xlpm.types,_xlpm.vals,"
                 +   "IF(ROWS(_xlpm.vals)<=1,_xlpm.vals,"
                 +     "_xlfn.LET("
                 +       "_xlpm.prev,_xlfn.VSTACK(INDEX(_xlpm.types,1),_xlfn.DROP(_xlpm.types,-1)),"
                 +       "_xlpm.gapCol,IF(_xlpm.types<>_xlpm.prev,\"\",_xlfn.NA()),"
                 +       "_xlfn.TOCOL(_xlfn.HSTACK(_xlpm.gapCol,_xlpm.vals),2)"
                 +     ")"
                 +   ")"
                 + $")({typesArg},{valsArg})";
        }

        // Data row — single cell per column, each spills downward via dynamic array
        int dataRow = headerRow + 1;

        // A: Type
        ws.Cell(dataRow, 1).FormulaA1 = $"IFERROR({Gap(ws_("A"))},\"\")";

        // B: Mfr
        ws.Cell(dataRow, 2).FormulaA1 = $"IFERROR({Gap(ws_("B"))},\"\")";

        // C: Catalog Number
        ws.Cell(dataRow, 3).FormulaA1 = $"IFERROR({Gap(ws_("C"))},\"\")";

        // D: Description (blank out 0 values, matching Quote behavior)
        ws.Cell(dataRow, 4).FormulaA1 = $"IFERROR({Gap($"IF({ws_("G")}=0,\"\",{ws_("G")})")},\"\")";

        // E: Qty + footer labels via VSTACK (IFERROR wraps only Gap so footer always anchors at bottom)
        ws.Cell(dataRow, 5).FormulaA1 =
            $"_xlfn.VSTACK(IFERROR({Gap(ws_("D"))},\"\")"
            + $",\"\",\"Subtotal:\",\"Freight:\",\"Grand Total:\")";

        // F: Sell Ea.
        ws.Cell(dataRow, 6).FormulaA1 = $"IFERROR({Gap(sellEa)},\"\")";

        // G: Sell Ext. + footer values via VSTACK (IFERROR wraps only Gap)
        ws.Cell(dataRow, 7).FormulaA1 =
            $"_xlfn.VSTACK(IFERROR({Gap(sellExt)},\"\")"
            + $",\"\",{subtotal},0,{subtotal}+0)";

        // Format currency columns
        ws.Column(6).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(7).Style.NumberFormat.Format = "$#,##0.00";

        // Column widths — pull auto-sized widths from Worksheet (FILTER formulas can't auto-size)
        ws.Column(1).Width = wsSheet.Column(WsColType).Width;
        ws.Column(2).Width = wsSheet.Column(WsColMfr).Width;
        ws.Column(3).Width = wsSheet.Column(WsColCatalog).Width;
        ws.Column(4).Width = wsSheet.Column(WsColDesc).Width;
        ws.Column(5).Width = 8;
        ws.Column(6).Width = 12;
        ws.Column(7).Width = 14;

        // Print setup
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.SetRowsToRepeatAtTop(1, headerRow);
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

            if (existing != null)
            {
                // Preserve user columns
                ws.Cell(row, WsColCalc).Value = existing.Calc;
                if (existing.Phase.HasValue)
                    ws.Cell(row, WsColPhase).Value = existing.Phase.Value;

                // Shared pricing logic
                WritePricingCells(ws, row, canonicalRow, catalog, pricing,
                    existing.Description, existing.UnitCost, existing.Markup, existing.Tariff, existing.Adder,
                    existing.DescIsFormula, existing.CostIsFormula, existing.MarkupIsFormula, existing.TariffIsFormula, existing.AdderIsFormula,
                    isNewRow: false);
            }
            else
            {
                // New row — no existing data
                WritePricingCells(ws, row, canonicalRow, catalog, pricing,
                    null, null, null, null, null, false, false, false, false, false, isNewRow: true);

                // For brand-new types, Prev Qty = 0 so delta shows +qty (green-highlight semantic)
                if (isNewType)
                    ws.Cell(row, WsColPrevQty).Value = 0;

                // Highlight Revit-side cells only: green if entire type is new, yellow if just a new catalog.
                // Fill stops at Delta so pricing team's editable columns are not visually disrupted.
                var fillColor = isNewType ? GreenFill : YellowFill;
                for (int col = 1; col <= WsColDelta; col++)
                    ws.Cell(row, col).Style.Fill.BackgroundColor = fillColor;
            }

            // Mark canonical only when the catalog has siblings on this sheet
            if (row == canonicalRow && catalogCounts.GetValueOrDefault(catalog) > 1)
                MarkCanonicalRow(ws, row, catalog);

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

            // Red fill + strikethrough
            for (int col = 1; col <= WsColAdder; col++)
            {
                ws.Cell(row, col).Style.Fill.BackgroundColor = RedFill;
                ws.Cell(row, col).Style.Font.Strikethrough = true;
            }

            row++;
        }

        // Visual separator between TurboSuite (locked) and pricing (editable) columns
        ws.Column(WsColDelta).Style.Border.RightBorder = XLBorderStyleValues.Thick;

        // Hide gridlines so only the explicit type-group dividers read as separators
        ws.ShowGridLines = false;

        // Light gray divider at the last row of each Type group
        ApplyTypeGroupDividers(ws, 2, row - 1);

        // Re-apply protection
        for (int r = 2; r < row; r++)
        {
            for (int col = WsColDesc; col <= WsColPhase; col++)
                ws.Cell(r, col).Style.Protection.SetLocked(false);
        }
        ws.Protect().AllowElement(XLSheetProtectionElements.FormatColumns);
    }

    private static readonly XLColor AutoFilledFontColor = XLColor.FromHtml("#808080");

    private static void MarkCanonicalRow(IXLWorksheet ws, int row, string catalog)
    {
        ws.Cell(row, WsColCatalog).GetComment().AddText(
            $"Source row for catalog {catalog}. Edit here; other rows with this catalog auto-fill from this one.");

        ws.Cell(row, WsColDesc).Style.Font.Bold = true;
        ws.Cell(row, WsColUnitCost).Style.Font.Bold = true;
        ws.Cell(row, WsColMarkup).Style.Font.Bold = true;
        ws.Cell(row, WsColTariff).Style.Font.Bold = true;
        ws.Cell(row, WsColAdder).Style.Font.Bold = true;
    }

    private static void StyleAutoFilledCell(IXLCell cell)
    {
        cell.Style.Font.Italic = true;
        cell.Style.Font.FontColor = AutoFilledFontColor;
    }

    private static void WritePricingCells(
        IXLWorksheet ws, int row, int canonicalRow, string catalog,
        Dictionary<string, PricingEntry>? pricing,
        string? existingDesc, double? existingCost, double? existingMarkup, double? existingTariff, double? existingAdder,
        bool descIsFormula, bool costIsFormula, bool markupIsFormula, bool tariffIsFormula, bool adderIsFormula,
        bool isNewRow)
    {
        bool isCanonical = row == canonicalRow;

        if (isCanonical)
        {
            // Canonical row: write literal values — existing first, then pricing lookup, then blank.
            if (!isNewRow)
            {
                if (existingDesc != null) ws.Cell(row, WsColDesc).Value = existingDesc;
                if (existingCost.HasValue) ws.Cell(row, WsColUnitCost).Value = existingCost.Value;
                if (existingMarkup.HasValue) ws.Cell(row, WsColMarkup).Value = existingMarkup.Value;
                if (existingTariff.HasValue) ws.Cell(row, WsColTariff).Value = existingTariff.Value;
                if (existingAdder.HasValue) ws.Cell(row, WsColAdder).Value = existingAdder.Value;
            }
            else if (pricing != null && pricing.TryGetValue(catalog, out var pe))
            {
                ws.Cell(row, WsColDesc).Value = pe.Description;
                ws.Cell(row, WsColUnitCost).Value = pe.Cost;
            }
            return;
        }

        // Non-canonical row: per field, preserve user-entered literals; otherwise formula-ref canonical.
        if (!isNewRow && !descIsFormula && existingDesc != null)
        {
            ws.Cell(row, WsColDesc).Value = existingDesc;
        }
        else
        {
            ws.Cell(row, WsColDesc).FormulaA1 = $"G{canonicalRow}";
            StyleAutoFilledCell(ws.Cell(row, WsColDesc));
        }

        if (!isNewRow && !costIsFormula && existingCost.HasValue)
        {
            ws.Cell(row, WsColUnitCost).Value = existingCost.Value;
        }
        else
        {
            ws.Cell(row, WsColUnitCost).FormulaA1 = $"I{canonicalRow}";
            StyleAutoFilledCell(ws.Cell(row, WsColUnitCost));
        }

        if (!isNewRow && !markupIsFormula && existingMarkup.HasValue)
        {
            ws.Cell(row, WsColMarkup).Value = existingMarkup.Value;
        }
        else
        {
            ws.Cell(row, WsColMarkup).FormulaA1 = $"J{canonicalRow}";
            StyleAutoFilledCell(ws.Cell(row, WsColMarkup));
        }

        if (!isNewRow && !tariffIsFormula && existingTariff.HasValue)
        {
            ws.Cell(row, WsColTariff).Value = existingTariff.Value;
        }
        else
        {
            ws.Cell(row, WsColTariff).FormulaA1 = $"K{canonicalRow}";
            StyleAutoFilledCell(ws.Cell(row, WsColTariff));
        }

        if (!isNewRow && !adderIsFormula && existingAdder.HasValue)
        {
            ws.Cell(row, WsColAdder).Value = existingAdder.Value;
        }
        else
        {
            ws.Cell(row, WsColAdder).FormulaA1 = $"L{canonicalRow}";
            StyleAutoFilledCell(ws.Cell(row, WsColAdder));
        }
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

    private static void ReorderSheets(IXLWorkbook wb)
    {
        // Canonical order: Cover, Worksheet, Quote, Phase 1, Phase 2, Phase 3, Changes, Counts (by date ascending)
        var ordered = new List<string>();
        foreach (var name in new[] { "Cover", "Worksheet", "Quote", "Phase 1", "Phase 2", "Phase 3", "Changes" })
        {
            if (wb.Worksheets.TryGetWorksheet(name, out _))
                ordered.Add(name);
        }

        var countsSheets = wb.Worksheets
            .Where(s => s.Name.StartsWith("Counts ", StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => s.Name);
        ordered.AddRange(countsSheets);

        int pos = 1;
        foreach (var name in ordered)
        {
            if (wb.Worksheets.TryGetWorksheet(name, out var ws))
                ws.Position = pos++;
        }
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
                Calc = ws.Cell(r, WsColCalc).GetString(),
                PrevQty = ReadCachedDouble(ws.Cell(r, WsColQty)),
                Phase = ReadNumericCell(ws.Cell(r, WsColPhase)),
                Description = ws.Cell(r, WsColDesc).HasFormula ? null : ws.Cell(r, WsColDesc).GetString(),
                UnitCost = ReadNumericCell(ws.Cell(r, WsColUnitCost)),
                Markup = ReadNumericCell(ws.Cell(r, WsColMarkup)),
                Tariff = ReadNumericCell(ws.Cell(r, WsColTariff)),
                Adder = ReadNumericCell(ws.Cell(r, WsColAdder)),
                DescIsFormula = ws.Cell(r, WsColDesc).HasFormula,
                CostIsFormula = ws.Cell(r, WsColUnitCost).HasFormula,
                MarkupIsFormula = ws.Cell(r, WsColMarkup).HasFormula,
                TariffIsFormula = ws.Cell(r, WsColTariff).HasFormula,
                AdderIsFormula = ws.Cell(r, WsColAdder).HasFormula,
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
        public bool CostIsFormula { get; init; }
        public bool MarkupIsFormula { get; init; }
        public bool TariffIsFormula { get; init; }
        public bool AdderIsFormula { get; init; }
        public bool IsStrikethrough { get; init; }
    }

    #endregion

    #region Dynamic Array Post-Processing

    /// <summary>
    /// Patches the saved .xlsx to enable dynamic array spilling for Phase sheet formulas.
    /// ClosedXML doesn't write the XLDAPR cell metadata that Excel requires, so formulas
    /// containing FILTER/VSTACK get the implicit intersection operator (@) added on open.
    /// This method injects xl/metadata.xml with the dynamic array property definition and
    /// adds cm="1" to the formula cells in each Phase sheet.
    /// </summary>
    private static void PatchDynamicArrayMetadata(string filePath, List<string> phaseSheetNames)
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
        var sheetRIds = new Dictionary<string, string>();
        foreach (var sheet in workbook.Root!.Descendants(sml + "sheet"))
        {
            string name = sheet.Attribute("name")?.Value ?? "";
            string rId = sheet.Attribute(orel + "id")?.Value ?? "";
            if (phaseSheetNames.Contains(name))
                sheetRIds[name] = rId;
        }

        // Map rIds to file targets
        foreach (var sheetName in phaseSheetNames)
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

            // Mark every formula cell as a dynamic array formula
            foreach (var cell in sheetDoc.Descendants(sml + "c"))
            {
                var f = cell.Element(sml + "f");
                if (f == null) continue;

                // cm="1" on the cell references the XLDAPR cellMetadata entry
                if (cell.Attribute("cm") == null)
                    cell.SetAttributeValue("cm", "1");

                // Array formula attributes on the <f> element
                string cellRef = cell.Attribute("r")?.Value ?? "";
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

    #endregion
}
