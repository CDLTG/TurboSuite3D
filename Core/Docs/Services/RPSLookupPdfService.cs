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
    // ── Page (letter only) ──
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
    private const double HeaderLogoHeight       = 76;
    private const double HeaderLogoRightInset   = -18;
    private const double HeaderHeight           = 50;
    private const double HeaderSpacing          = 10;

    // ── Table ──
    private const double RowHeight        = 18;
    private const double HeaderRowHeight  = 22;
    private const double CellPaddingLeft  = 6;
    private const double FontSize         = 8.5;
    private const double HeaderFontSize   = 8.5;

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
        DocsSettings settings)
    {
        using var pdf = new PdfDocument();
        pdf.Info.Title = $"{projectName} Power Supply Lookup Table";
        GeneratePages(pdf, instances, projectName, settings);
        pdf.Save(outputPath);
    }

    /// <summary>
    /// Generate lookup table pages into an existing PdfDocument (for combined output).
    /// </summary>
    public static void GeneratePages(
        PdfDocument pdf,
        List<RPSInstanceModel> instances,
        string projectName,
        DocsSettings settings)
    {
        string logoPath = settings.LogoFilePath;
        double contentWidth = PageWidth - MarginLeft - MarginRight;

        var fontHeader   = new XFont("Segoe UI", HeaderProjectFontSize, XFontStyle.Bold);
        var fontSubtitle = new XFont("Segoe UI", HeaderSubtitleFontSize);
        var fontColHead  = new XFont("Segoe UI", HeaderFontSize, XFontStyle.Bold);
        var fontCell     = new XFont("Segoe UI", FontSize);
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

        // Calculate column widths proportionally
        double totalWeight = columns.Sum(c => c.Weight);
        double[] colWidths = columns.Select(c => contentWidth * c.Weight / totalWeight).ToArray();
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
            page.Width  = XUnit.FromPoint(PageWidth);
            page.Height = XUnit.FromPoint(PageHeight);
            gfx = XGraphics.FromPdfPage(page);
            y = MarginTop;

            // ── Header ─��
            gfx.DrawString(projectName, fontHeader, XBrushes.Black,
                new XPoint(MarginLeft, y + HeaderProjectFontSize));
            gfx.DrawString("POWER SUPPLY LOOKUP TABLE", fontSubtitle, XBrushes.Black,
                new XPoint(MarginLeft, y + HeaderProjectFontSize + HeaderSubtitleFontSize + 3));

            if (logo != null)
            {
                double logoW, logoH;
                if (logo is XPdfForm pdfLogo)
                {
                    logoH = HeaderLogoHeight;
                    logoW = pdfLogo.PointWidth * (logoH / pdfLogo.PointHeight);
                }
                else
                {
                    logoH = HeaderLogoHeight;
                    logoW = (double)logo.PixelWidth * (logoH / logo.PixelHeight);
                }
                double logoX = PageWidth - MarginRight - logoW - HeaderLogoRightInset;
                double logoY = y + (HeaderProjectFontSize + HeaderSubtitleFontSize - logoH) / 2 + 4;
                if (logo is XPdfForm pdfForm)
                    DrawScaledForm(gfx, pdfForm, logoX, logoY, logoW, logoH);
                else
                    gfx.DrawImage(logo, logoX, logoY, logoW, logoH);
            }

            y += HeaderHeight + HeaderSpacing;

            // ── Column headers ──
            gfx.DrawRectangle(new XSolidBrush(HeaderBgColor),
                MarginLeft, y, contentWidth, HeaderRowHeight);

            for (int c = 0; c < columns.Length; c++)
            {
                gfx.DrawString(columns[c].Header, fontColHead, XBrushes.White,
                    new XPoint(colX[c] + CellPaddingLeft, y + HeaderRowHeight - 6));
            }
            y += HeaderRowHeight;
        }

        StartNewPage();

        // ── Data rows ──
        for (int r = 0; r < instances.Count; r++)
        {
            if (y + RowHeight > PageHeight - MarginBottom - FooterHeight)
                StartNewPage();

            // Alternating row shading
            if (r % 2 == 1)
            {
                gfx!.DrawRectangle(new XSolidBrush(AltRowColor),
                    MarginLeft, y, contentWidth, RowHeight);
            }

            // Bottom rule
            gfx!.DrawLine(penRule, MarginLeft, y + RowHeight, MarginLeft + contentWidth, y + RowHeight);

            // Cell values
            var instance = instances[r];
            for (int c = 0; c < columns.Length; c++)
            {
                string value = columns[c].Selector(instance);
                if (string.IsNullOrEmpty(value)) continue;

                // Shrink font if text exceeds column width
                var cellFont = fontCell;
                double maxCellWidth = colWidths[c] - CellPaddingLeft * 2;
                double textWidth = gfx.MeasureString(value, cellFont).Width;
                if (textWidth > maxCellWidth)
                {
                    double scale = maxCellWidth / textWidth;
                    cellFont = new XFont("Segoe UI", FontSize * scale);
                }

                gfx.DrawString(value, cellFont, XBrushes.Black,
                    new XPoint(colX[c] + CellPaddingLeft, y + RowHeight - 5));
            }

            y += RowHeight;
        }

        gfx?.Dispose();

        // Footer + page numbers
        int lastPageIndex = pdf.PageCount - 1;
        int totalPages = lastPageIndex - firstPageIndex + 1;
        for (int i = firstPageIndex; i <= lastPageIndex; i++)
        {
            using var g = XGraphics.FromPdfPage(pdf.Pages[i]);
            DrawFooter(g, settings, fontPageNum, i - firstPageIndex + 1, totalPages);
        }

        logoStream?.Dispose();
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

        gfx.DrawString($"Page {pageNumber} of {pageCount}", fontPageNum, XBrushes.Gray,
            new XPoint(PageWidth - MarginRight, fTop + 10), XStringFormats.TopRight);
    }
}
