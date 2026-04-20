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
    /// Creates a new Counts workbook. Rep directory path comes from TurboDocs user settings
    /// (CountsViewModel passes it); seeds Dashboard!B4 and is used to build the Rep Lists sheet.
    /// Descriptions and pricing are no longer auto-filled — pricing team enters them manually.
    /// </summary>
    public static void GenerateNew(
        List<CountsFixtureModel> fixtures,
        string projectName,
        string projectLocation,
        string outputPath,
        string repDirectoryPath)
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
    }

    /// <summary>
    /// Opens an existing workbook and updates it with fresh Revit data.
    /// </summary>
    public static void GenerateUpdate(
        List<CountsFixtureModel> fixtures,
        string existingPath,
        string repDirectoryPath)
    {
        using var wb = new XLWorkbook(existingPath);

        string dateString = DateTime.Now.ToString("yyyy.MM.dd");
        string countsSheetName = ResolveCountsSheetName(wb, dateString);

        // Find previous Counts sheet for change detection
        var prevCountsSheet = FindLatestCountsSheet(wb);
        var prevData = prevCountsSheet != null ? ReadCountsSheetData(prevCountsSheet) : null;

        // Migrate legacy workbooks (no Dashboard) and seed/keep Rep Directory path.
        EnsureDashboardSheet(wb, repDirectoryPath);

        // Dashboard!B4 is authoritative; fall back to settings only when empty.
        string effectiveRepPath = ReadRepDirectoryPathFromDashboard(wb);
        if (string.IsNullOrWhiteSpace(effectiveRepPath))
            effectiveRepPath = repDirectoryPath;
        var repDirectory = ReadRepDirectory(effectiveRepPath);

        // Read existing Worksheet rows
        var existingRows = ReadExistingWorksheetRows(wb);

        // Build new Counts sheet
        BuildCountsSheet(wb, fixtures, countsSheetName);

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
        ws.PageSetup.PrintAreas.Add("A1:B17");
    }

    #endregion

    #region Dashboard Sheet

    // Dashboard cell anchors (consumed by helper pipeline via named ranges)
    private const string DashRepDirCell = "B4";
    private const string DashLutronCell = "B8";
    private const string DashFreightCell = "B9";
    private const string DashBidDateCell = "B12";
    private const string DashReleaseDateCell = "B13";
    private const string DashNotesFirstRow = "36";
    private const string DashNotesLastRow = "50";

    private static readonly XLColor HeaderBlue = XLColor.FromHtml("#4472C4");

    /// <summary>
    /// Creates the Dashboard sheet holding all workbook configuration, quote adjustments,
    /// bid lock state, internal notes, and the quote-footer notes library. All named ranges
    /// used by the helper pipeline and external readers are defined here.
    /// </summary>
    private static void BuildDashboardSheet(IXLWorkbook wb, string projectName, string repDirectoryPath)
    {
        var ws = wb.Worksheets.Add("Dashboard");
        ws.Position = 2; // after Cover

        // Title bar (rows 1-2)
        ws.Range("A1:D2").Merge();
        ws.Cell("A1").Value = $"COUNTS DASHBOARD — {projectName}";
        StyleSectionBar(ws.Range("A1:D2"), fontSize: 14);

        // --- CONFIGURATION ---
        WriteSectionBar(ws, 3, "CONFIGURATION");
        ws.Cell("A4").Value = "Rep Directory Path";
        ws.Range("B4:D4").Merge();
        ws.Cell("B4").Value = repDirectoryPath ?? string.Empty;

        // --- QUOTE ADJUSTMENTS ---
        WriteSectionBar(ws, 7, "QUOTE ADJUSTMENTS");
        // Left blank by default — the Quote/Phase sheets omit the Lutron row when B8 is empty,
        // and blank cells coerce to 0 inside the Grand Total arithmetic.
        ws.Cell("A8").Value = "Lutron Lighting Control";
        ws.Cell("B8").Style.NumberFormat.Format = "$#,##0.00";

        ws.Cell("A9").Value = "Estimated Freight";
        ws.Cell("B9").Style.NumberFormat.Format = "$#,##0.00";

        // --- BID LOCK ---
        WriteSectionBar(ws, 11, "BID LOCK");
        ws.Cell("A12").Value = "Bid Quote Date";
        ws.Cell("B12").Style.NumberFormat.Format = "yyyy-mm-dd";

        ws.Cell("A13").Value = "Release Lock";
        ws.Cell("B13").Style.NumberFormat.Format = "yyyy-mm-dd";

        ws.Cell("A14").Value = "Bid Status";
        ws.Cell("B14").FormulaA1 =
            "IF(B12=\"\",\"Unlocked\",IF(B13=B12,\"Pending release\",\"LOCKED \"&TEXT(B12,\"yyyy-mm-dd\")))";
        ws.Cell("B14").Style.Font.Italic = true;

        // --- INTERNAL NOTES ---
        WriteSectionBar(ws, 16, "INTERNAL NOTES");
        ws.Cell("A17").Value = "Date";
        ws.Cell("B17").Value = "Author";
        ws.Cell("C17").Value = "Status";
        ws.Cell("D17").Value = "Notes";
        var notesHdr = ws.Range("A17:D17");
        notesHdr.Style.Font.Bold = true;
        notesHdr.Style.Fill.BackgroundColor = XLColor.FromHtml("#D9E1F2");

        // --- QUOTE FOOTER NOTES ---
        WriteSectionBar(ws, 34, "QUOTE FOOTER NOTES");
        ws.Cell("A35").Value = "#";
        ws.Cell("B35").Value = "BOLD";
        ws.Cell("C35").Value = "Notes";
        var fnHdr = ws.Range("A35:C35");
        fnHdr.Style.Font.Bold = true;
        fnHdr.Style.Fill.BackgroundColor = XLColor.FromHtml("#D9E1F2");

        for (int i = 0; i < 15; i++)
        {
            int r = 36 + i;
            ws.Cell(r, 1).Value = i + 1;
            ws.Cell(r, 2).Value = false; // boolean literal — pass 3 upgrades to native checkbox
            ws.Cell(r, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        // Column widths
        ws.Column(1).Width = 26;
        ws.Column(2).Width = 18;
        ws.Column(3).Width = 42;
        ws.Column(4).Width = 40;

        // Named ranges
        wb.DefinedNames.Add("RepDirectoryPath", ws.Range("B4:B4"));
        wb.DefinedNames.Add("LutronSubtotal", ws.Range("B8:B8"));
        wb.DefinedNames.Add("Freight", ws.Range("B9:B9"));
        wb.DefinedNames.Add("BidDate", ws.Range("B12:B12"));
        wb.DefinedNames.Add("ReleaseDate", ws.Range("B13:B13"));
        wb.DefinedNames.Add("QuoteNotes", ws.Range($"C{DashNotesFirstRow}:C{DashNotesLastRow}"));
        wb.DefinedNames.Add("QuoteNotesBold", ws.Range($"B{DashNotesFirstRow}:B{DashNotesLastRow}"));

        // Protection: unlock editable cells, lock the rest
        foreach (string addr in new[] { "B4", "B8", "B9", "B12", "B13" })
            ws.Cell(addr).Style.Protection.SetLocked(false);
        ws.Range("A18:D32").Style.Protection.SetLocked(false);
        ws.Range($"B{DashNotesFirstRow}:C{DashNotesLastRow}").Style.Protection.SetLocked(false);
        ws.Protect().AllowElement(XLSheetProtectionElements.FormatColumns);

        ws.ShowGridLines = false;
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

    private static void WriteSectionBar(IXLWorksheet ws, int row, string text)
    {
        var rng = ws.Range(row, 1, row, 4);
        rng.Merge();
        rng.FirstCell().Value = text;
        StyleSectionBar(rng, fontSize: 11);
    }

    private static void StyleSectionBar(IXLRange rng, double fontSize)
    {
        rng.Style.Fill.BackgroundColor = HeaderBlue;
        rng.Style.Font.FontColor = XLColor.White;
        rng.Style.Font.Bold = true;
        rng.Style.Font.FontSize = fontSize;
        rng.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        rng.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
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

        // Flatten fixtures to (Type, Mfr, Catalog, Qty) rows
        var rows = new List<(string Type, string Mfr, string Catalog, int Qty)>();
        foreach (var f in fixtures)
        {
            for (int c = 0; c < 6; c++)
            {
                string cat = f.CatalogNumbers[c] ?? "";
                if (string.IsNullOrWhiteSpace(cat)) continue;
                rows.Add((f.TypeMark, f.Manufacturer, cat, f.Count));
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
                colMax, ref repBarMax, ref quoteMax, ref orderMax);
            curRow++; // spacer
            ws.PageSetup.AddHorizontalPageBreak(curRow);
        }

        if (unmatched.Count > 0 || matched.Count == 0)
        {
            curRow = WriteRepBlock(ws, curRow, "UNMATCHED MANUFACTURERS", string.Empty, string.Empty,
                projectName, projectLocation, unmatched,
                XLColor.FromHtml("#F1A983"), colMax, ref repBarMax, ref quoteMax, ref orderMax);
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

        ws.PageSetup.PageOrientation = XLPageOrientation.Portrait;
        ws.PageSetup.FitToPages(1, 0);
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
        int[] colMax, ref int repBarMax, ref int quoteMax, ref int orderMax)
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
                $"SUMIFS(Worksheet!D:D,Worksheet!A:A,A{row},Worksheet!C:C,C{row})";

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
                ws.Cell(row, WsColMfr).Value = TrimMfrForDisplay(f.Manufacturer);
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
                if (!typeFirstRow.ContainsKey(f.TypeMark))
                    typeFirstRow[f.TypeMark] = row;
                else
                    WriteTariffCell(ws, row, typeFirstRow[f.TypeMark], existingTariff: null, isNewRow: true);

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
        ApplyQtyColumnFormatting(ws);
        ws.Column(WsColDesc).Width = 25;
        ws.Column(WsColCalc).Width = 10;
        ws.Column(WsColUnitCost).Width = 12;
        ws.Column(WsColMarkup).Width = 10;
        ws.Column(WsColTariff).Width = 10;
        ws.Column(WsColAdder).Width = 10;
        ws.Column(WsColPhase).Width = 10;

        ApplyPricingColumnFormats(ws);

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

    // Currency/percent number formats for the pricing columns. Applied from both build and
    // update paths because update clears row 2's cell-level formats — falling back to column
    // style isn't reliable once cells have been written with numeric values.
    private static void ApplyPricingColumnFormats(IXLWorksheet ws)
    {
        ws.Column(WsColUnitCost).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(WsColMarkup).Style.NumberFormat.Format = "0%";
        ws.Column(WsColTariff).Style.NumberFormat.Format = "0%";
        ws.Column(WsColAdder).Style.NumberFormat.Format = "$#,##0.00";
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

            var rng = ws.Range(r, 1, r, WsColPhase);
            rng.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            rng.Style.Border.BottomBorderColor = XLColor.LightGray;
        }
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
        // Exclude "dependent" placeholder — it's a visual cue on Worksheet for drag-fill links,
        // not a real description. Treated as blank here so it doesn't leak into print sheets.
        string catalogCombined =
            $"{Col("C")}&IF(({Col("G")}<>0)*({Col("G")}<>\"\")*({Col("G")}<>\"dependent\"),\" ~ \"&{Col("G")},\"\")";

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

        // Quote footer notes — appended to the Type column under the Grand Total line.
        // FILTER drops empty rows so users can fill fewer than 15 notes without blank spill.
        string notesSpill = "_xlfn._xlws.FILTER(QuoteNotes,QuoteNotes<>\"\",\"\")";

        int i = 0;
        // Type — blank tariff row, plus notes appended at the bottom (after Grand Total row footprint)
        ws.Cell($"{cols[i++]}2").FormulaA1 =
            $"_xlfn.VSTACK(IFERROR({Gap(Col("A"), "\"\"")},\"\"),\"\",\"\",\"\",\"\",\"\",{notesSpill})";
        // Mfr — blank tariff row
        ws.Cell($"{cols[i++]}2").FormulaA1 = $"IFERROR({Gap(Col("B"), "\"\"")},\"\")";
        // Catalog~Desc — "Tariff" label on tariff row
        ws.Cell($"{cols[i++]}2").FormulaA1 = $"IFERROR({Gap(catalogCombined, "\"Tariff\"")},\"\")";
        // Qty — blank tariff row
        ws.Cell($"{cols[i++]}2").FormulaA1 = $"IFERROR({Gap(Col("D"), "\"\"")},\"\")";
        // Delta (Quote only) — blank tariff row
        if (includeDelta)
            ws.Cell($"{cols[i++]}2").FormulaA1 = $"IFERROR({Gap(Col("F"), "\"\"")},\"\")";
        // Sell Ea. + footer labels (Subtotal / [Lutron?] / Freight / Grand Total).
        // Lutron row is omitted when Dashboard!LutronSubtotal is blank.
        string labelFooter =
            "IF(LutronSubtotal=\"\","
            + "_xlfn.VSTACK(\"\",\"Subtotal:\",\"Freight:\",\"Grand Total:\"),"
            + "_xlfn.VSTACK(\"\",\"Subtotal:\",\"Lutron Control:\",\"Freight:\",\"Grand Total:\"))";
        ws.Cell($"{cols[i++]}2").FormulaA1 =
            $"_xlfn.VSTACK(IFERROR({Gap(sellEa, "\"\"")},\"\"),{labelFooter})";
        // Sell Ext. + footer values (tariff row carries per-type tariff amount).
        // Grand Total uses N(LutronSubtotal) so a blank cell contributes zero.
        string valueFooter =
            "IF(LutronSubtotal=\"\","
            + $"_xlfn.VSTACK(\"\",{subtotal},Freight,{subtotal}+Freight),"
            + $"_xlfn.VSTACK(\"\",{subtotal},LutronSubtotal,Freight,{subtotal}+LutronSubtotal+Freight))";
        ws.Cell($"{cols[i++]}2").FormulaA1 =
            $"_xlfn.VSTACK(IFERROR({Gap(sellExt, "_xlpm.totals*_xlpm.pcts")},\"\"),{valueFooter})";
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

        WritePrintSheetTitle(ws, 7, "\"PRODUCT PRICING \"&Cover!B11");

        int headerRow = 6;
        string[] headers = { "Type", "Mfr", "Catalog Number", "Qty", "Δ", "Sell Ea.", "Sell Ext." };
        WritePrintSheetHeaders(ws, headerRow, headers);

        // Spill row — one ANCHORARRAY per column pointing at Worksheet!AAn..AGn.
        // Mfr column (index 1) is wrapped in UPPER for all-caps display, with a
        // hardcoded substitution: "Environmental Lights" → "LUMEN SPEC".
        int spillRow = headerRow + 1;
        for (int i = 0; i < QuoteHelperCols.Length; i++)
        {
            string anchor = $"_xlfn.ANCHORARRAY(Worksheet!{QuoteHelperCols[i]}2)";
            ws.Cell(spillRow, i + 1).FormulaA1 = i == 1 ? BuildMfrDisplayFormula(anchor) : anchor;
        }

        // Currency + delta formats
        ws.Column(6).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(7).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(5).Style.NumberFormat.Format = "+0;-0;;@";
        ws.Column(2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

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
        {
            string anchor = $"_xlfn.ANCHORARRAY(Worksheet!{cols[i]}2)";
            ws.Cell(spillRow, i + 1).FormulaA1 = i == 1 ? BuildMfrDisplayFormula(anchor) : anchor;
        }

        // Currency formats
        ws.Column(5).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(6).Style.NumberFormat.Format = "$#,##0.00";
        ws.Column(2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

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
            // Clear contents + formats on the visible columns A..helper-last. The helper-pipeline
            // cells at row 2 will be overwritten by WriteHelperPipeline at the end of this method.
            ws.Range(2, 1, 2, WsColHelperLast).Clear(XLClearOptions.Contents | XLClearOptions.NormalFormats);
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
            ws.Cell(row, WsColQty).FormulaA1 = BuildQtyFormula(row, csRef);
            ws.Cell(row, WsColDelta).FormulaA1 = $"IF(E{row}=\"\",\"\",D{row}-E{row})";
            ws.Cell(row, WsColCalc).GetDataValidation().List("\"Reel,Channel,End Cap,Clip\"", true);

            // Prev Qty — prefer the previous Worksheet's cached Qty (reflects Calc adjustments),
            // else recompute from the preserved canonical Calc + prev fixture lengths (cache may
            // be missing on 3rd+ passes — ClosedXML doesn't emit caches for rewritten formulas),
            // else fall back to raw Count for types that weren't present before.
            if (existing?.PrevQty.HasValue == true)
            {
                ws.Cell(row, WsColPrevQty).Value = existing.PrevQty.Value;
            }
            else if (prevData != null && prevData.TryGetValue(type, out var prevFixture))
            {
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
            ws.Cell(row, WsColMfr).Value = TrimMfrForDisplay(mfr);
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

        ApplyQtyColumnFormatting(ws);
        ApplyPricingColumnFormats(ws);

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
                $"IF(K{typeCanonicalRow}=\"\",\"\",K{typeCanonicalRow})";
            StyleAutoFilledCell(ws.Cell(row, WsColTariff));
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

    private static string ReadRepDirectoryPathFromDashboard(IXLWorkbook wb)
    {
        if (!wb.Worksheets.TryGetWorksheet("Dashboard", out var ws))
            return string.Empty;
        return ws.Cell("B4").GetString();
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
