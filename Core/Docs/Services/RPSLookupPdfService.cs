using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

public static class RPSLookupPdfService
{
    // ── Page (Large = construction strip, Small = 8.5x11 letter) ──
    // The strip's width is content-fit at generation time (see GeneratePages), so no
    // fixed large width constant — only its 28.5" height is pinned here.
    private const double LargePageHeight = 28.5 * 72;   // 2052 pt
    private const double SmallPageWidth  = 8.5  * 72;   // 612 pt
    private const double SmallPageHeight = 11.0 * 72;   // 792 pt

    // ── Margins ──
    private const double MarginLeft   = 36;
    private const double MarginRight  = 36;
    private const double MarginTop    = 28;
    private const double MarginBottom = 28;

    // ── Header (letter — full branding) ──
    private const double HeaderProjectFontSize  = 18;
    private const double HeaderSubtitleFontSize = 12;
    private const double HeaderLogoHeight       = 76;
    private const double HeaderLogoRightInset   = -18;
    private const double HeaderHeight           = 50;
    private const double HeaderSpacing          = 10;

    // ── Header (large — condensed construction-strip title; design polish TBD) ──
    // Title reuses the size the project name previously used here, non-bold.
    private const double CompactTitleFontSize = 10;
    private const double CompactHeaderHeight  = 20;
    private const double CompactHeaderSpacing = 2;   // title/headers hug the rule

    // ── Table (letter) ──
    private const double RowHeight        = 18;
    private const double HeaderRowHeight  = 22;
    private const double FontSize         = 8.5;
    private const double HeaderFontSize   = 8.5;

    // ── Table (large — condensed rows so more fits per strip) ──
    private const double CompactRowHeight       = 14;
    private const double CompactHeaderRowHeight  = 14;
    private const double CompactFontSize         = 7.5;

    private const double CellPaddingLeft  = 6;

    // Trailing slack past each content-fit column on the strip — the main dial for how
    // tightly columns pack (the next column's CellPaddingLeft adds to it). Large only.
    private const double CompactColumnGap = 12;

    // ── Footer ──
    private const double FooterHeight = 28;

    // ── Colors ──
    private static readonly XColor AltRowColor = XColor.FromGrayScale(0.95);
    private static readonly XColor HeaderBgColor = XColor.FromGrayScale(0.15);
    private static readonly XColor RuleColor = XColor.FromGrayScale(0.80);

    /// <summary>
    /// Generate a standalone lookup table PDF.
    /// </summary>
    public static void Generate(
        List<RPSInstanceModel> instances,
        string projectName,
        string outputPath,
        bool useLargeFormat,
        DocsSettings settings)
    {
        using var pdf = new PdfDocument();
        pdf.Info.Title = $"{projectName} Power Supply Lookup Table";
        GeneratePages(pdf, instances, projectName, useLargeFormat, settings);
        pdf.Save(outputPath);
    }

    /// <summary>
    /// Generate lookup table pages into an existing PdfDocument (for combined output).
    /// </summary>
    public static void GeneratePages(
        PdfDocument pdf,
        List<RPSInstanceModel> instances,
        string projectName,
        bool useLargeFormat,
        DocsSettings settings)
    {
        string logoPath = settings.LogoFilePath;

        // Format-dependent metrics. The 8.5x28.5 construction strip trades branding
        // for density: a one-line title band, tighter rows, and no footer (it is a
        // field reference, not a deliverable — matches the fixture/RPS schedules).
        double pageHeight = useLargeFormat ? LargePageHeight : SmallPageHeight;
        double rowHeight       = useLargeFormat ? CompactRowHeight      : RowHeight;
        double headerRowHeight = useLargeFormat ? CompactHeaderRowHeight : HeaderRowHeight;
        double cellFontSize    = useLargeFormat ? CompactFontSize       : FontSize;
        double footerReserve   = useLargeFormat ? 0                     : FooterHeight;

        var fontHeader   = new XFont("Segoe UI", HeaderProjectFontSize, XFontStyle.Bold);
        var fontSubtitle = new XFont("Segoe UI", HeaderSubtitleFontSize);
        var fontCompactTitle = new XFont("Segoe UI", CompactTitleFontSize);
        var fontColHead  = new XFont("Segoe UI", cellFontSize, XFontStyle.Bold);
        var fontCell     = new XFont("Segoe UI", cellFontSize);
        var fontPageNum  = new XFont("Segoe UI Light", 7);

        var penRule = new XPen(RuleColor, 0.5);

        // Column definitions: (Header, DataSelector, ProportionalWidth)
        var columns = new (string Header, Func<RPSInstanceModel, string> Selector, double Weight)[]
        {
            ("Number",         r => r.SwitchID,       1.0),
            ("Type",            r => r.TypeMark,        1.0),
            ("Catalog Number", r => r.CatalogNumber,   2.5),
            ("Load Name",      r => r.LoadName,        2.0),
            ("Circuit",        r => r.CircuitNumber,   1.0),
        };

        // ── Column widths + page width ──
        // Letter keeps fixed proportional shares of the page content width. The 8.5x28.5
        // strip instead content-fits each column (measure the header and every cell) and
        // lets the overall strip width follow — collapsing the wasted horizontal gaps
        // while, because the widths come from measurement, never clipping content.
        double pageWidth;
        double contentWidth;
        double[] colWidths = new double[columns.Length];

        if (useLargeFormat)
        {
            using var tempPdf = new PdfDocument();
            var tempPage = tempPdf.AddPage();
            using var tempGfx = XGraphics.FromPdfPage(tempPage);

            for (int c = 0; c < columns.Length; c++)
            {
                double maxWidth = tempGfx.MeasureString(columns[c].Header, fontColHead).Width;
                foreach (var inst in instances)
                {
                    string value = columns[c].Selector(inst);
                    if (string.IsNullOrEmpty(value)) continue;
                    double w = tempGfx.MeasureString(value, fontCell).Width;
                    if (w > maxWidth) maxWidth = w;
                }
                colWidths[c] = CellPaddingLeft + maxWidth + CompactColumnGap;
            }

            double tableWidth = colWidths.Sum();
            double titleWidth = tempGfx.MeasureString("POWER SUPPLY LOOKUP TABLE", fontCompactTitle).Width;
            contentWidth = Math.Max(tableWidth, titleWidth);
            pageWidth = MarginLeft + contentWidth + MarginRight;
        }
        else
        {
            pageWidth = SmallPageWidth;
            contentWidth = pageWidth - MarginLeft - MarginRight;
            double totalWeight = columns.Sum(c => c.Weight);
            colWidths = columns.Select(c => contentWidth * c.Weight / totalWeight).ToArray();
        }

        double[] colX = new double[columns.Length];
        colX[0] = MarginLeft;
        for (int i = 1; i < columns.Length; i++)
            colX[i] = colX[i - 1] + colWidths[i - 1];

        // Load logo
        XImage? logo = null;
        MemoryStream? logoStream = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(logoPath) && File.Exists(logoPath))
            {
                if (logoPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    logoStream = new MemoryStream(File.ReadAllBytes(logoPath));
                    logo = XPdfForm.FromStream(logoStream);
                }
                else
                {
                    logo = XImage.FromFile(logoPath);
                }
            }
        }
        catch { /* logo remains null */ }

        int firstPageIndex = pdf.PageCount;
        XGraphics? gfx = null;
        double y = 0;

        void StartNewPage()
        {
            gfx?.Dispose();
            var page = pdf.AddPage();
            page.Width  = XUnit.FromPoint(pageWidth);
            page.Height = XUnit.FromPoint(pageHeight);
            gfx = XGraphics.FromPdfPage(page);
            y = MarginTop;

            if (useLargeFormat)
                DrawCompactHeader(gfx, contentWidth, fontCompactTitle, penRule, ref y);
            else
                DrawLetterHeader(gfx, projectName, pageWidth,
                    fontHeader, fontSubtitle, logo, ref y);

            // ── Column headers ──
            if (useLargeFormat)
            {
                // Utilitarian strip: plain black header text, no band.
                for (int c = 0; c < columns.Length; c++)
                    gfx.DrawString(columns[c].Header, fontColHead, XBrushes.Black,
                        new XPoint(colX[c] + CellPaddingLeft, y + headerRowHeight - 6));
            }
            else
            {
                gfx.DrawRectangle(new XSolidBrush(HeaderBgColor),
                    MarginLeft, y, contentWidth, headerRowHeight);
                for (int c = 0; c < columns.Length; c++)
                    gfx.DrawString(columns[c].Header, fontColHead, XBrushes.White,
                        new XPoint(colX[c] + CellPaddingLeft, y + headerRowHeight - 6));
            }
            y += headerRowHeight;
        }

        StartNewPage();

        // ── Data rows ──
        // Separator rules are drawn at the TOP of each row, AFTER that row's shading,
        // so an alternating-shade rectangle can never paint over an adjacent rule —
        // every separator lands at the same thin weight. (Drawing them at the bottom
        // let the next row's shading cover the top edge of the rule, making rules
        // under shaded rows look heavier than those under white rows.)
        bool firstRowOnPage = true;
        bool anyRowOnPage = false;

        // Close off the row block on the current page with a bottom border.
        void DrawBottomBorder()
        {
            if (anyRowOnPage)
                gfx!.DrawLine(penRule, MarginLeft, y, MarginLeft + contentWidth, y);
        }

        for (int r = 0; r < instances.Count; r++)
        {
            if (y + rowHeight > pageHeight - MarginBottom - footerReserve)
            {
                DrawBottomBorder();
                StartNewPage();
                firstRowOnPage = true;
                anyRowOnPage = false;
            }

            // Alternating row shading
            if (r % 2 == 1)
            {
                gfx!.DrawRectangle(new XSolidBrush(AltRowColor),
                    MarginLeft, y, contentWidth, rowHeight);
            }

            // Separator rule (top of row, on top of the shading). None above the first
            // row on a page — the column header sits directly above it.
            if (!firstRowOnPage)
                gfx!.DrawLine(penRule, MarginLeft, y, MarginLeft + contentWidth, y);

            // Cell values
            var instance = instances[r];
            for (int c = 0; c < columns.Length; c++)
            {
                string value = columns[c].Selector(instance);
                if (string.IsNullOrEmpty(value)) continue;

                // Shrink font if text exceeds column width
                var cellFont = fontCell;
                double maxCellWidth = colWidths[c] - CellPaddingLeft * 2;
                double textWidth = gfx!.MeasureString(value, cellFont).Width;
                if (textWidth > maxCellWidth)
                {
                    double scale = maxCellWidth / textWidth;
                    cellFont = new XFont("Segoe UI", cellFontSize * scale);
                }

                gfx!.DrawString(value, cellFont, XBrushes.Black,
                    new XPoint(colX[c] + CellPaddingLeft, y + rowHeight - 5));
            }

            y += rowHeight;
            firstRowOnPage = false;
            anyRowOnPage = true;
        }

        DrawBottomBorder();
        gfx?.Dispose();

        // Footer + page numbers. Suppressed on the 8.5x28.5 construction strip, which
        // ships unfooted as a field reference (matches the fixture/RPS schedules).
        if (!useLargeFormat)
        {
            int lastPageIndex = pdf.PageCount - 1;
            int totalPages = lastPageIndex - firstPageIndex + 1;
            for (int i = firstPageIndex; i <= lastPageIndex; i++)
            {
                using var g = XGraphics.FromPdfPage(pdf.Pages[i]);
                DrawFooter(g, pageHeight, pageWidth, settings, fontPageNum, i - firstPageIndex + 1, totalPages);
            }
        }

        logoStream?.Dispose();
    }

    private static void DrawLetterHeader(XGraphics gfx, string projectName, double pageWidth,
        XFont fontHeader, XFont fontSubtitle, XImage? logo, ref double y)
    {
        gfx.DrawString(projectName, fontHeader, XBrushes.Black,
            new XPoint(MarginLeft, y + HeaderProjectFontSize));
        gfx.DrawString("POWER SUPPLY LOOKUP TABLE", fontSubtitle, XBrushes.Black,
            new XPoint(MarginLeft, y + HeaderProjectFontSize + HeaderSubtitleFontSize + 3));

        if (logo != null)
        {
            double logoH = HeaderLogoHeight;
            double logoW = logo is XPdfForm pdfLogo
                ? pdfLogo.PointWidth * (logoH / pdfLogo.PointHeight)
                : (double)logo.PixelWidth * (logoH / logo.PixelHeight);
            double logoX = pageWidth - MarginRight - logoW - HeaderLogoRightInset;
            double logoY = y + (HeaderProjectFontSize + HeaderSubtitleFontSize - logoH) / 2 + 4;
            if (logo is XPdfForm pdfForm)
                DrawScaledForm(gfx, pdfForm, logoX, logoY, logoW, logoH);
            else
                gfx.DrawImage(logo, logoX, logoY, logoW, logoH);
        }

        y += HeaderHeight + HeaderSpacing;
    }

    /// <summary>
    /// Condensed title band for the construction strip: the table title in plain black
    /// (no project name, no logo) above a thin rule. Utilitarian field reference —
    /// deliberately minimal; polish is deferred.
    /// </summary>
    private static void DrawCompactHeader(XGraphics gfx, double contentWidth,
        XFont fontTitle, XPen penRule, ref double y)
    {
        double ruleY = y + CompactHeaderHeight - 2;

        // Baseline just above the rule so the title hugs it (rather than floating at the
        // top of the band). The header row below hugs the rule from the other side via
        // the small CompactHeaderSpacing.
        gfx.DrawString("POWER SUPPLY LOOKUP TABLE", fontTitle, XBrushes.Black,
            new XPoint(MarginLeft, ruleY - 4));

        gfx.DrawLine(penRule, MarginLeft, ruleY, MarginLeft + contentWidth, ruleY);

        y += CompactHeaderHeight + CompactHeaderSpacing;
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

    private static void DrawFooter(XGraphics gfx, double pageHeight, double pageWidth,
        DocsSettings settings, XFont fontPageNum, int pageNumber, int pageCount)
    {
        double fTop = pageHeight - FooterHeight;

        gfx.DrawLine(new XPen(XColor.FromGrayScale(0.8), 0.25),
            MarginLeft, fTop + 2, pageWidth - MarginLeft, fTop + 2);

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
                new XPoint(pageWidth / 2, fTop + 10), XStringFormats.TopCenter);
        }

        gfx.DrawString($"Page {pageNumber} of {pageCount}", fontPageNum, XBrushes.Gray,
            new XPoint(pageWidth - MarginRight, fTop + 10), XStringFormats.TopRight);
    }
}
