using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

public static class LoadsPdfService
{
    // Install the PDFsharp font resolver before any XFont/XGraphics render (PDFsharp 6.x core
    // has no built-in system-font resolution). Idempotent; runs once before any static entry.
    static LoadsPdfService() => PdfFontResolver.EnsureRegistered();

    #region Layout Constants

    // ── Page (always letter) ──
    private const double PageWidth  = 8.5 * 72;   // 612 pt
    private const double PageHeight = 11.0 * 72;  // 792 pt

    // ── Margins ──
    private const double MarginLeft   = 36;
    private const double MarginRight  = 36;
    private const double MarginTop    = 28;
    private const double MarginBottom = 28;

    // ── Header ──
    private const double HeaderProjectFontSize  = 18;
    private const double HeaderSubtitleFontSize = 12;
    private const double HeaderNoteFontSize     = 8;
    private const double HeaderLogoHeight       = 50;
    private const double HeaderLogoRightInset   = -10;
    private const double HeaderHeight           = 50;
    private const double HeaderSpacing          = 5;

    // ── Table ──
    private const double RowFontSize       = 9;
    private const double HeaderFontSize    = 8;
    private const double LineHeight        = 16;
    private const double ColumnHeaderHeight = 13;
    private const double BaselineOffset    = 11;
    private const double ColumnPadding     = 6;

    // ── Room bands (By Room export) ──
    private const double RoomHeaderHeight   = 18;
    private const double RoomHeaderFontSize = 9;
    private const double RoomGap            = 8;

    // Column-header placement on the By-Room export: true = repeated under every room band
    // (matches the Lutron reference); false = drawn once at the top of each page. This is the
    // single toggle called out in the plan — flip it and nothing else changes.
    // static readonly (not const) on purpose: it keeps both placement branches live to the
    // compiler, so flipping the toggle needs no other edit and neither branch trips CS0162.
    private static readonly bool ColumnHeadersPerRoom = true;

    // ── Footer ──
    private const double FooterHeight = 28;

    // ── Construction strip (By Circuit only — 8.5x28.5, mirrors the RPS lookup strip) ──
    // A flush field reference, not a branded deliverable: 28.5" tall page, content-fit columns
    // so the strip width follows the data, a one-line title band, condensed rows, and no footer.
    private const double StripPageHeight   = 28.5 * 72;  // 2052 pt
    private const double StripMargin       = 2;          // hair border, near edge-to-edge
    private const double StripTitleFontSize = 10;
    private const double StripHeaderHeight  = 20;        // title band height
    private const double StripHeaderSpacing = 2;         // title/table hug the rule
    private const double StripRowHeight     = 14;
    private const double StripHeaderRowHeight = 14;
    private const double StripFontSize      = 7.5;
    private const double StripCellPadding   = 6;
    private const double StripColumnGap     = 12;        // trailing slack past each content-fit column

    #endregion

    private const double ContentWidth = PageWidth - MarginLeft - MarginRight;
    private const double UsableBottom = PageHeight - FooterHeight;

    // Column headers, left-to-right. Index 0 ("Ckt") is centered; the rest are left-aligned.
    private static readonly string[] Headers =
        { "Ckt", "Load", "Dimming", "Fixtures", "Qty", "Driver", "Watts" };

    // ──────────────────────────────────────────────────────────────────────────────────────
    // By Circuit — a single flat list (unchanged output).
    // ──────────────────────────────────────────────────────────────────────────────────────
    public static void Generate(
        List<LoadsCircuitModel> circuits,
        string projectName,
        string outputPath,
        DocsSettings settings)
    {
        var fontRow       = new XFont("Segoe UI", RowFontSize);
        var fontColHeader = new XFont("Segoe UI", HeaderFontSize, XFontStyleEx.Bold);
        var fontPageNum   = new XFont("Segoe UI Light", 7);
        var gridPen       = new XPen(XColor.FromGrayScale(0.85), 0.5);
        var centerData    = new XStringFormat { Alignment = XStringAlignment.Center, LineAlignment = XLineAlignment.BaseLine };

        var (colX, colW) = ComputeColumns(circuits, fontRow, fontColHeader);

        XImage? logo = LoadLogo(settings, out MemoryStream? logoStream);

        using var pdf = new PdfDocument();
        pdf.Info.Title = $"{projectName} Load Schedule";

        PdfPage? page = null;
        XGraphics? gfx = null;
        double y = 0;

        void StartNewPage()
        {
            gfx?.Dispose();
            page = NewPage(pdf, out gfx);
            y = MarginTop;
            DrawPageHeaderBlock(gfx!, projectName, logo, ref y);
            DrawColumnHeaders(gfx!, colX, colW, fontColHeader, ByCircuitHeaderRule, ref y);
        }

        StartNewPage();

        foreach (var circuit in circuits)
        {
            if (y + LineHeight > UsableBottom)
                StartNewPage();
            DrawRow(gfx!, circuit, colX, colW, fontRow, gridPen, centerData, ref y);
        }

        gfx?.Dispose();
        WriteFooters(pdf, settings, fontPageNum);
        pdf.Save(outputPath);
        logoStream?.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // By Room — circuits grouped into room sections, each under a gray band.
    // ──────────────────────────────────────────────────────────────────────────────────────
    public static void GenerateByRoom(
        List<LoadSection> sections,
        string projectName,
        string outputPath,
        DocsSettings settings)
    {
        var fontRow        = new XFont("Segoe UI", RowFontSize);
        var fontColHeader  = new XFont("Segoe UI", HeaderFontSize, XFontStyleEx.Bold);
        var fontRoomHeader = new XFont("Segoe UI", RoomHeaderFontSize, XFontStyleEx.Bold);
        var fontPageNum    = new XFont("Segoe UI Light", 7);
        var gridPen        = new XPen(XColor.FromGrayScale(0.85), 0.5);
        var centerData     = new XStringFormat { Alignment = XStringAlignment.Center, LineAlignment = XLineAlignment.BaseLine };

        var allCircuits = sections.SelectMany(s => s.Circuits).ToList();
        var (colX, colW) = ComputeColumns(allCircuits, fontRow, fontColHeader);

        XImage? logo = LoadLogo(settings, out MemoryStream? logoStream);

        using var pdf = new PdfDocument();
        pdf.Info.Title = $"{projectName} Load Schedule";

        PdfPage? page = null;
        XGraphics? gfx = null;
        double y = 0;

        void StartNewPage()
        {
            gfx?.Dispose();
            page = NewPage(pdf, out gfx);
            y = MarginTop;
            DrawPageHeaderBlock(gfx!, projectName, logo, ref y);
            // Column headers live under each room band when ColumnHeadersPerRoom; otherwise they
            // lead the page like the By-Circuit export.
            if (!ColumnHeadersPerRoom)
                DrawColumnHeaders(gfx!, colX, colW, fontColHeader, ByRoomHeaderRule, ref y);
        }

        // Draw a room band (+ per-room column headers) at the current y, redrawing on continuation.
        void DrawBand(LoadSection section, bool continued)
        {
            DrawRoomBand(gfx!, section, continued, fontRoomHeader, ref y);
            if (ColumnHeadersPerRoom)
                DrawColumnHeaders(gfx!, colX, colW, fontColHeader, ByRoomHeaderRule, ref y);
        }

        StartNewPage();

        double bandBlock = RoomHeaderHeight + (ColumnHeadersPerRoom ? ColumnHeaderHeight : 0) + LineHeight;
        // Box around each room's on-page segment — matching the Panel Schedule's module outline.
        // A room can span pages, so the box is closed per page segment (band → last row on the page).
        var boxPen = new XPen(XColor.FromGrayScale(0.50), 0.75);

        foreach (var section in sections)
        {
            // Keep-with-next: never orphan a band (+ its column headers + at least one row) at the
            // bottom of a page.
            if (y + bandBlock > UsableBottom)
                StartNewPage();

            double segmentTop = y;
            DrawBand(section, continued: false);
            // Bookmark the section on the page where it opens.
            pdf.Outlines.Add(section.RoomName, page!);

            foreach (var circuit in section.Circuits)
            {
                if (y + LineHeight > UsableBottom)
                {
                    // Close the box around this page's segment before the break, then reopen it
                    // under the continuation band on the next page.
                    gfx!.DrawRectangle(boxPen, MarginLeft, segmentTop, ContentWidth, y - segmentTop);
                    StartNewPage();
                    segmentTop = y;
                    DrawBand(section, continued: true);
                }
                DrawRow(gfx!, circuit, colX, colW, fontRow, gridPen, centerData, ref y);
            }

            gfx!.DrawRectangle(boxPen, MarginLeft, segmentTop, ContentWidth, y - segmentTop);
            y += RoomGap;
        }

        gfx?.Dispose();
        WriteFooters(pdf, settings, fontPageNum);
        pdf.Save(outputPath);
        logoStream?.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────────────────────────
    // By Circuit — construction strip (8.5x28.5). Same flat list and columns as Generate, but a
    // content-fit, condensed, unfooted field-reference layout modeled on RPSLookupPdfService's
    // large format. Deliberately independent of the Letter path so that shipped output is untouched.
    // ──────────────────────────────────────────────────────────────────────────────────────
    public static void GenerateByCircuitConstruction(
        List<LoadsCircuitModel> circuits,
        string projectName,
        string outputPath,
        DocsSettings settings)
    {
        var fontTitle   = new XFont("Segoe UI", StripTitleFontSize);
        var fontColHead = new XFont("Segoe UI", StripFontSize, XFontStyleEx.Bold);
        var fontCell    = new XFont("Segoe UI", StripFontSize);
        var penRule     = new XPen(XColor.FromGrayScale(0.80), 0.5);
        var altRowBrush = new XSolidBrush(XColor.FromGrayScale(0.95));

        // (Header, cell selector). Same seven columns as the Letter By-Circuit export; all
        // left-aligned on the strip (the RPS reference left-aligns everything, incl. its number col).
        var columns = new (string Header, Func<LoadsCircuitModel, string> Selector)[]
        {
            ("Ckt",      c => c.CircuitNumber),
            ("Load",     c => c.LoadName),
            ("Dimming",  c => c.DimmingProtocol),
            ("Fixtures", c => c.FixturesDisplay),
            ("Qty",      c => c.QuantityDisplay),
            ("Driver",   c => c.DriverDisplay),
            ("Watts",    c => c.TotalWattsDisplay),
        };

        // ── Content-fit each column (header + every cell), strip width follows ──
        double[] colW = new double[columns.Length];
        double contentW, titleWidth;
        using (var tempPdf = new PdfDocument())
        {
            var tempPage = tempPdf.AddPage();
            using var tempGfx = XGraphics.FromPdfPage(tempPage);

            for (int c = 0; c < columns.Length; c++)
            {
                double maxWidth = tempGfx.MeasureString(columns[c].Header, fontColHead).Width;
                foreach (var circuit in circuits)
                {
                    string value = columns[c].Selector(circuit);
                    if (string.IsNullOrEmpty(value)) continue;
                    double w = tempGfx.MeasureString(value, fontCell).Width;
                    if (w > maxWidth) maxWidth = w;
                }
                colW[c] = StripCellPadding + maxWidth + StripColumnGap;
            }

            titleWidth = tempGfx.MeasureString("LOAD SCHEDULE", fontTitle).Width;
        }

        contentW = Math.Max(colW.Sum(), titleWidth);
        double pageW = StripMargin + contentW + StripMargin;

        double[] colX = new double[columns.Length];
        colX[0] = StripMargin;
        for (int i = 1; i < columns.Length; i++)
            colX[i] = colX[i - 1] + colW[i - 1];

        RenderStrip(circuits, columns, projectName, outputPath,
            pageW, contentW, colX, colW, fontTitle, fontColHead, fontCell, penRule, altRowBrush);
    }

    private static void RenderStrip(
        List<LoadsCircuitModel> circuits,
        (string Header, Func<LoadsCircuitModel, string> Selector)[] columns,
        string projectName,
        string outputPath,
        double pageW, double contentW,
        double[] colX, double[] colW,
        XFont fontTitle, XFont fontColHead, XFont fontCell, XPen penRule, XBrush altRowBrush)
    {
        using var pdf = new PdfDocument();
        pdf.Info.Title = $"{projectName} Load Schedule";

        XGraphics? gfx = null;
        double y = 0;

        void StartNewPage()
        {
            gfx?.Dispose();
            var page = pdf.AddPage();
            page.Width  = XUnit.FromPoint(pageW);
            page.Height = XUnit.FromPoint(StripPageHeight);
            gfx = XGraphics.FromPdfPage(page);
            y = StripMargin;

            // Title band: plain black title above a thin rule (no logo, no verify note).
            double ruleY = y + StripHeaderHeight - 2;
            gfx.DrawString("LOAD SCHEDULE", fontTitle, XBrushes.Black,
                new XPoint(StripMargin, ruleY - 4));
            gfx.DrawLine(penRule, StripMargin, ruleY, StripMargin + contentW, ruleY);
            y += StripHeaderHeight + StripHeaderSpacing;

            // Column headers: plain black, no band.
            for (int c = 0; c < columns.Length; c++)
                gfx.DrawString(columns[c].Header, fontColHead, XBrushes.Black,
                    new XPoint(colX[c] + StripCellPadding, y + StripHeaderRowHeight - 6));
            y += StripHeaderRowHeight;
        }

        StartNewPage();

        bool firstRowOnPage = true;
        bool anyRowOnPage = false;

        void DrawBottomBorder()
        {
            if (anyRowOnPage)
                gfx!.DrawLine(penRule, StripMargin, y, StripMargin + contentW, y);
        }

        for (int r = 0; r < circuits.Count; r++)
        {
            if (y + StripRowHeight > StripPageHeight - StripMargin)
            {
                DrawBottomBorder();
                StartNewPage();
                firstRowOnPage = true;
                anyRowOnPage = false;
            }

            // Alternating shade, then the top separator on top of it (so shading never covers a
            // rule); no rule above the first row on a page — the header sits directly above it.
            if (r % 2 == 1)
                gfx!.DrawRectangle(altRowBrush, StripMargin, y, contentW, StripRowHeight);
            if (!firstRowOnPage)
                gfx!.DrawLine(penRule, StripMargin, y, StripMargin + contentW, y);

            var circuit = circuits[r];
            for (int c = 0; c < columns.Length; c++)
            {
                string value = columns[c].Selector(circuit);
                if (string.IsNullOrEmpty(value)) continue;

                // Shrink-to-fit: content-fit widths never clip, but a per-cell guard mirrors RPS.
                var cellFont = fontCell;
                double maxCellWidth = colW[c] - StripCellPadding * 2;
                double textWidth = gfx!.MeasureString(value, cellFont).Width;
                if (textWidth > maxCellWidth && maxCellWidth > 0)
                    cellFont = new XFont("Segoe UI", StripFontSize * (maxCellWidth / textWidth));

                gfx!.DrawString(value, cellFont, XBrushes.Black,
                    new XPoint(colX[c] + StripCellPadding, y + StripRowHeight - 5));
            }

            y += StripRowHeight;
            firstRowOnPage = false;
            anyRowOnPage = true;
        }

        DrawBottomBorder();
        gfx?.Dispose();

        // No footer — the strip ships unfooted as a field reference (matches the RPS strip).
        pdf.Save(outputPath);
    }

    // ── Shared rendering helpers ──────────────────────────────────────────────────────────

    private static PdfPage NewPage(PdfDocument pdf, out XGraphics gfx)
    {
        var page = pdf.AddPage();
        page.Width  = XUnit.FromPoint(PageWidth);
        page.Height = XUnit.FromPoint(PageHeight);
        gfx = XGraphics.FromPdfPage(page);
        return page;
    }

    /// <summary>Measure column widths from the circuit set. Load flexes to fill the remainder.</summary>
    private static (double[] colX, double[] colW) ComputeColumns(
        IReadOnlyList<LoadsCircuitModel> circuits, XFont fontRow, XFont fontColHeader)
    {
        double colCircuit, colDimming, colFixtures, colQuantity, colDriver, colWattage;

        using (var tempPdf = new PdfDocument())
        {
            var tempPage = tempPdf.AddPage();
            using var tempGfx = XGraphics.FromPdfPage(tempPage);

            double maxCircuit = 0, maxDimming = 0, maxFixtures = 0;
            double maxQuantity = 0, maxDriver = 0, maxWattage = 0;

            foreach (var c in circuits)
            {
                maxCircuit  = Math.Max(maxCircuit,  tempGfx.MeasureString(c.CircuitNumber, fontRow).Width);
                maxDimming  = Math.Max(maxDimming,  tempGfx.MeasureString(c.DimmingProtocol, fontRow).Width);
                maxFixtures = Math.Max(maxFixtures, tempGfx.MeasureString(c.FixturesDisplay, fontRow).Width);
                maxQuantity = Math.Max(maxQuantity, tempGfx.MeasureString(c.QuantityDisplay, fontRow).Width);
                maxDriver   = Math.Max(maxDriver,   tempGfx.MeasureString(c.DriverDisplay, fontRow).Width);
                maxWattage  = Math.Max(maxWattage,  tempGfx.MeasureString(c.TotalWattsDisplay, fontRow).Width);
            }

            double[] hw = new double[7];
            for (int i = 0; i < 7; i++)
                hw[i] = tempGfx.MeasureString(Headers[i], fontColHeader).Width;

            double minPad = ColumnPadding * 4; // extra breathing room for non-Load columns
            colCircuit  = Math.Max(maxCircuit,  hw[0]) + minPad;
            colDimming  = Math.Max(maxDimming,  hw[2]) + minPad;
            colFixtures = Math.Max(maxFixtures, hw[3]) + minPad;
            colQuantity = Math.Max(maxQuantity, hw[4]) + minPad;
            colDriver   = Math.Max(maxDriver,   hw[5]) + minPad;
            colWattage  = Math.Max(maxWattage,  hw[6]) + minPad;
        }

        double fixedCols = colCircuit + colDimming + colFixtures + colQuantity + colDriver + colWattage;
        double colLoad = Math.Max(ContentWidth - fixedCols, 60);

        double[] colW = { colCircuit, colLoad, colDimming, colFixtures, colQuantity, colDriver, colWattage };
        double[] colX = new double[7];
        colX[0] = MarginLeft;
        for (int i = 1; i < 7; i++)
            colX[i] = colX[i - 1] + colW[i - 1];

        return (colX, colW);
    }

    private static XImage? LoadLogo(DocsSettings settings, out MemoryStream? logoStream)
    {
        logoStream = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.LogoFilePath) && File.Exists(settings.LogoFilePath))
            {
                if (settings.LogoFilePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    logoStream = new MemoryStream(File.ReadAllBytes(settings.LogoFilePath));
                    return XPdfForm.FromStream(logoStream);
                }
                return XImage.FromFile(settings.LogoFilePath);
            }
        }
        catch { /* logo remains null */ }
        return null;
    }

    /// <summary>Project name + subtitle (left), logo (right), verify note. Assumes y is at MarginTop;
    /// advances y past the header band.</summary>
    private static void DrawPageHeaderBlock(XGraphics gfx, string projectName, XImage? logo, ref double y)
    {
        var fontHeaderProject  = new XFont("Segoe UI", HeaderProjectFontSize, XFontStyleEx.Bold);
        var fontHeaderSubtitle = new XFont("Segoe UI", HeaderSubtitleFontSize);
        var fontHeaderNote     = new XFont("Segoe UI Light", HeaderNoteFontSize);
        var brushHeaderNote    = new XSolidBrush(XColor.FromGrayScale(0.40));

        gfx.DrawString(projectName, fontHeaderProject, XBrushes.Black,
            new XPoint(MarginLeft, y + HeaderProjectFontSize));
        gfx.DrawString("LOAD SCHEDULE", fontHeaderSubtitle, XBrushes.Black,
            new XPoint(MarginLeft, y + HeaderProjectFontSize + HeaderSubtitleFontSize + 3));

        if (logo != null)
        {
            double logoH = HeaderLogoHeight;
            double logoW = logo is XPdfForm pdfLogo
                ? pdfLogo.PointWidth * (logoH / pdfLogo.PointHeight)
                : (double)logo.PixelWidth * (logoH / logo.PixelHeight);
            double logoX = PageWidth - MarginRight - logoW - HeaderLogoRightInset;
            double logoY = y + (HeaderProjectFontSize + HeaderSubtitleFontSize - logoH) / 2;
            if (logo is XPdfForm pdfForm)
                DrawScaledForm(gfx, pdfForm, logoX, logoY, logoW, logoH);
            else
                gfx.DrawImage(logo, logoX, logoY, logoW, logoH);
        }

        double noteY = y + HeaderProjectFontSize + HeaderSubtitleFontSize + 16;
        gfx.DrawString(
            "Note: Verify load schedule with official control system documentation.",
            fontHeaderNote, brushHeaderNote, new XPoint(MarginLeft, noteY));

        y += HeaderHeight + HeaderSpacing;
    }

    // The two column-header underlines: By Circuit keeps the heavier divider; By Room matches the
    // Panel Schedule's lighter, thinner rule (its room box already carries the visual weight).
    private static readonly XPen ByCircuitHeaderRule = new XPen(XColor.FromGrayScale(0.50), 0.75);
    private static readonly XPen ByRoomHeaderRule    = new XPen(XColor.FromGrayScale(0.85), 0.5);

    private static void DrawColumnHeaders(XGraphics gfx, double[] colX, double[] colW,
        XFont fontColHeader, XPen underlinePen, ref double y)
    {
        var headerBrush = new XSolidBrush(XColor.FromGrayScale(0.15));
        var centerAlign = new XStringFormat { Alignment = XStringAlignment.Center, LineAlignment = XLineAlignment.BaseLine };
        gfx.DrawString(Headers[0], fontColHeader, headerBrush,
            new XPoint(colX[0] + colW[0] / 2, y + BaselineOffset - 2), centerAlign);
        for (int i = 1; i < 7; i++)
            gfx.DrawString(Headers[i], fontColHeader, headerBrush,
                new XPoint(colX[i] + ColumnPadding, y + BaselineOffset - 2));
        y += ColumnHeaderHeight;
        gfx.DrawLine(underlinePen, MarginLeft, y, PageWidth - MarginRight, y);
    }

    /// <summary>Gray room band: name (uppercase) left, room total wattage right. A "(No Room)"
    /// section prints no wattage. Advances y past the band.</summary>
    private static void DrawRoomBand(XGraphics gfx, LoadSection section, bool continued,
        XFont fontRoomHeader, ref double y)
    {
        gfx.DrawRectangle(new XSolidBrush(XColor.FromGrayScale(0.88)),
            MarginLeft, y, ContentWidth, RoomHeaderHeight);

        string label = section.RoomName.ToUpperInvariant() + (continued ? " (continued)" : "");
        double centerY = y + RoomHeaderHeight / 2;
        gfx.DrawString(label, fontRoomHeader, XBrushes.Black,
            new XPoint(MarginLeft + ColumnPadding, centerY),
            new XStringFormat { Alignment = XStringAlignment.Near, LineAlignment = XLineAlignment.Center });

        if (!section.IsNoRoom)
        {
            double watts = section.Circuits.Sum(c => c.ApparentLoadVA);
            gfx.DrawString($"{Math.Round(watts)} W", fontRoomHeader, XBrushes.Black,
                new XPoint(PageWidth - MarginRight - ColumnPadding, centerY),
                new XStringFormat { Alignment = XStringAlignment.Far, LineAlignment = XLineAlignment.Center });
        }

        y += RoomHeaderHeight;
    }

    private static void DrawRow(XGraphics gfx, LoadsCircuitModel circuit, double[] colX, double[] colW,
        XFont fontRow, XPen gridPen, XStringFormat centerData, ref double y)
    {
        double baseline = y + BaselineOffset;

        // Circuit (centered)
        gfx.DrawString(circuit.CircuitNumber, fontRow, XBrushes.Black,
            new XPoint(colX[0] + colW[0] / 2, baseline), centerData);

        // Load (truncate if too wide)
        double loadMaxWidth = colW[1] - ColumnPadding * 2;
        string loadName = circuit.LoadName;
        if (gfx.MeasureString(loadName, fontRow).Width > loadMaxWidth && loadName.Length > 0)
        {
            while (loadName.Length > 1 && gfx.MeasureString(loadName + "…", fontRow).Width > loadMaxWidth)
                loadName = loadName[..^1];
            loadName += "…";
        }
        gfx.DrawString(loadName, fontRow, XBrushes.Black, new XPoint(colX[1] + ColumnPadding, baseline));

        gfx.DrawString(circuit.DimmingProtocol, fontRow, XBrushes.Black, new XPoint(colX[2] + ColumnPadding, baseline));
        gfx.DrawString(circuit.FixturesDisplay, fontRow, XBrushes.Black, new XPoint(colX[3] + ColumnPadding, baseline));
        gfx.DrawString(circuit.QuantityDisplay, fontRow, XBrushes.Black, new XPoint(colX[4] + ColumnPadding, baseline));
        gfx.DrawString(circuit.DriverDisplay, fontRow, XBrushes.Black, new XPoint(colX[5] + ColumnPadding, baseline));
        gfx.DrawString(circuit.TotalWattsDisplay, fontRow, XBrushes.Black, new XPoint(colX[6] + ColumnPadding, baseline));

        y += LineHeight;
        gfx.DrawLine(gridPen, MarginLeft, y, PageWidth - MarginRight, y);
    }

    private static void WriteFooters(PdfDocument pdf, DocsSettings settings, XFont fontPageNum)
    {
        for (int i = 0; i < pdf.PageCount; i++)
        {
            using var g = XGraphics.FromPdfPage(pdf.Pages[i]);
            DrawFooter(g, settings, fontPageNum, i + 1, pdf.PageCount);
        }
    }

    private static void DrawScaledForm(XGraphics gfx, XPdfForm form, double x, double y, double width, double height)
    {
        if (form.PointWidth <= 0 || form.PointHeight <= 0) return;
        var state = gfx.Save();
        gfx.TranslateTransform(x, y);
        gfx.ScaleTransform(width / form.PointWidth, height / form.PointHeight);
        gfx.DrawImage(form, 0, 0, form.PointWidth, form.PointHeight);
        gfx.Restore(state);
    }

    private static void DrawFooter(XGraphics gfx, DocsSettings settings, XFont fontPageNum,
        int pageNumber, int pageCount)
    {
        double fTop = PageHeight - FooterHeight;

        gfx.DrawLine(new XPen(XColor.FromGrayScale(0.8), 0.25),
            MarginLeft, fTop + 2, PageWidth - MarginLeft, fTop + 2);

        var font = new XFont("Segoe UI Light", 7.5);
        var brush = new XSolidBrush(XColor.FromGrayScale(0.45));

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.CompanyAddress)) parts.Add(settings.CompanyAddress);
        if (!string.IsNullOrWhiteSpace(settings.CompanyPhone)) parts.Add(settings.CompanyPhone);
        if (!string.IsNullOrWhiteSpace(settings.CompanyEmail)) parts.Add(settings.CompanyEmail);
        if (!string.IsNullOrWhiteSpace(settings.CompanyWebsite)) parts.Add(settings.CompanyWebsite);

        if (parts.Count > 0)
        {
            gfx.DrawString(string.Join("    |    ", parts), font, brush,
                new XPoint(PageWidth / 2, fTop + 10), XStringFormats.TopCenter);
        }

        // Release date left-aligned, mirroring the page number
        if (!string.IsNullOrWhiteSpace(settings.FooterDate))
            gfx.DrawString(settings.FooterDate, fontPageNum, XBrushes.Gray,
                new XPoint(MarginLeft, fTop + 10), XStringFormats.TopLeft);

        gfx.DrawString($"Page {pageNumber} of {pageCount}", fontPageNum, XBrushes.Gray,
            new XPoint(PageWidth - MarginRight, fTop + 10), XStringFormats.TopRight);
    }
}
