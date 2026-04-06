using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

public static class LoadsPdfService
{
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
    private const double HeaderLogoHeight       = 76;
    private const double HeaderLogoRightInset   = -18;
    private const double HeaderHeight           = 50;
    private const double HeaderSpacing          = 5;

    // ── Table ──
    private const double RowFontSize       = 9;
    private const double HeaderFontSize    = 8;
    private const double LineHeight        = 16;
    private const double ColumnHeaderHeight = 13;
    private const double BaselineOffset    = 11;
    private const double ColumnPadding     = 6;

    // ── Footer ──
    private const double FooterHeight = 28;

    #endregion

    public static void Generate(
        List<LoadsCircuitModel> circuits,
        string projectName,
        string outputPath,
        DocsSettings settings)
    {
        // Fonts
        var fontHeaderProject  = new XFont("Segoe UI", HeaderProjectFontSize, XFontStyle.Bold);
        var fontHeaderSubtitle = new XFont("Segoe UI", HeaderSubtitleFontSize);
        var fontHeaderNote     = new XFont("Segoe UI Light", HeaderNoteFontSize);
        var brushHeaderNote    = new XSolidBrush(XColor.FromGrayScale(0.40));

        var fontRow       = new XFont("Segoe UI", RowFontSize);
        var fontColHeader = new XFont("Segoe UI", HeaderFontSize, XFontStyle.Bold);
        var fontPageNum   = new XFont("Segoe UI Light", 7);

        var gridPen = new XPen(XColor.FromGrayScale(0.85), 0.5);

        // Column headers
        string[] headers = { "Ckt", "Load", "Dimming", "Fixtures", "Qty", "Driver", "Watts" };

        // ── Measurement pass: determine column widths ──
        double colCircuit, colLoad, colDimming, colFixtures, colQuantity, colDriver, colWattage;

        using (var tempPdf = new PdfDocument())
        {
            var tempPage = tempPdf.AddPage();
            using var tempGfx = XGraphics.FromPdfPage(tempPage);

            double maxCircuit = 0, maxDimming = 0, maxFixtures = 0;
            double maxQuantity = 0, maxDriver = 0, maxWattage = 0;

            foreach (var c in circuits)
            {
                double w = tempGfx.MeasureString(c.CircuitNumber, fontRow).Width;
                if (w > maxCircuit) maxCircuit = w;

                w = tempGfx.MeasureString(c.LoadClassification, fontRow).Width;
                if (w > maxDimming) maxDimming = w;

                w = tempGfx.MeasureString(c.FixturesDisplay, fontRow).Width;
                if (w > maxFixtures) maxFixtures = w;

                w = tempGfx.MeasureString(c.QuantityDisplay, fontRow).Width;
                if (w > maxQuantity) maxQuantity = w;

                w = tempGfx.MeasureString(c.DriverDisplay, fontRow).Width;
                if (w > maxDriver) maxDriver = w;

                w = tempGfx.MeasureString(c.TotalWattsDisplay, fontRow).Width;
                if (w > maxWattage) maxWattage = w;
            }

            // Measure header widths to enforce minimums
            double[] headerWidths = new double[7];
            for (int i = 0; i < 7; i++)
                headerWidths[i] = tempGfx.MeasureString(headers[i], fontColHeader).Width;

            double minPad = ColumnPadding * 4; // extra breathing room for non-Load columns
            colCircuit  = Math.Max(maxCircuit, headerWidths[0]) + minPad;
            colDimming  = Math.Max(maxDimming, headerWidths[2]) + minPad;
            colFixtures = Math.Max(maxFixtures, headerWidths[3]) + minPad;
            colQuantity = Math.Max(maxQuantity, headerWidths[4]) + minPad;
            colDriver   = Math.Max(maxDriver, headerWidths[5]) + minPad;
            colWattage  = Math.Max(maxWattage, headerWidths[6]) + minPad;
        }

        double contentWidth = PageWidth - MarginLeft - MarginRight;
        double fixedColsWidth = colCircuit + colDimming + colFixtures + colQuantity + colDriver + colWattage;
        colLoad = Math.Max(contentWidth - fixedColsWidth, 60);

        // Column X positions (left edge of each column)
        double[] colX = new double[7];
        double[] colW = { colCircuit, colLoad, colDimming, colFixtures, colQuantity, colDriver, colWattage };
        colX[0] = MarginLeft;
        for (int i = 1; i < 7; i++)
            colX[i] = colX[i - 1] + colW[i - 1];

        // Load logo
        XImage? logo = null;
        MemoryStream? logoStream = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.LogoFilePath) && File.Exists(settings.LogoFilePath))
            {
                if (settings.LogoFilePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    logoStream = new MemoryStream(File.ReadAllBytes(settings.LogoFilePath));
                    logo = XPdfForm.FromStream(logoStream);
                }
                else
                {
                    logo = XImage.FromFile(settings.LogoFilePath);
                }
            }
        }
        catch { /* logo remains null */ }

        using var pdf = new PdfDocument();
        pdf.Info.Title = $"{projectName} Load Schedule";

        PdfPage? page = null;
        XGraphics? gfx = null;
        double y = 0;

        void StartNewPage()
        {
            gfx?.Dispose();
            page = pdf.AddPage();
            page.Width  = XUnit.FromPoint(PageWidth);
            page.Height = XUnit.FromPoint(PageHeight);
            gfx = XGraphics.FromPdfPage(page);
            y = MarginTop;

            // Header: project name + subtitle (left), logo (right)
            gfx.DrawString(projectName, fontHeaderProject, XBrushes.Black,
                new XPoint(MarginLeft, y + HeaderProjectFontSize));
            gfx.DrawString("LOAD SCHEDULE", fontHeaderSubtitle, XBrushes.Black,
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

            double noteY = y + HeaderProjectFontSize + HeaderSubtitleFontSize + 16;
            gfx.DrawString(
                "Note: Verify load schedule with official control system documentation.",
                fontHeaderNote, brushHeaderNote, new XPoint(MarginLeft, noteY));

            y += HeaderHeight + HeaderSpacing;

            // ── Column headers ──
            var headerBrush = new XSolidBrush(XColor.FromGrayScale(0.15));
            // Ckt header centered
            var centerAlign = new XStringFormat
            {
                Alignment = XStringAlignment.Center,
                LineAlignment = XLineAlignment.BaseLine
            };
            gfx.DrawString(headers[0], fontColHeader, headerBrush,
                new XPoint(colX[0] + colW[0] / 2, y + BaselineOffset - 2), centerAlign);
            for (int i = 1; i < 7; i++)
            {
                gfx.DrawString(headers[i], fontColHeader, headerBrush,
                    new XPoint(colX[i] + ColumnPadding, y + BaselineOffset - 2));
            }
            y += ColumnHeaderHeight;

            // Line under column headers
            gfx.DrawLine(new XPen(XColor.FromGrayScale(0.50), 0.75),
                MarginLeft, y, PageWidth - MarginRight, y);
        }

        StartNewPage();

        var centerAlignData = new XStringFormat
        {
            Alignment = XStringAlignment.Center,
            LineAlignment = XLineAlignment.BaseLine
        };

        foreach (var circuit in circuits)
        {
            if (y + LineHeight > PageHeight - FooterHeight)
                StartNewPage();

            double baseline = y + BaselineOffset;

            // Circuit (centered)
            gfx!.DrawString(circuit.CircuitNumber, fontRow, XBrushes.Black,
                new XPoint(colX[0] + colW[0] / 2, baseline), centerAlignData);

            // Load (truncate if too wide)
            double loadMaxWidth = colW[1] - ColumnPadding * 2;
            string loadName = circuit.LoadName;
            if (gfx.MeasureString(loadName, fontRow).Width > loadMaxWidth && loadName.Length > 0)
            {
                while (loadName.Length > 1 && gfx.MeasureString(loadName + "\u2026", fontRow).Width > loadMaxWidth)
                    loadName = loadName[..^1];
                loadName += "\u2026";
            }
            gfx.DrawString(loadName, fontRow, XBrushes.Black,
                new XPoint(colX[1] + ColumnPadding, baseline));

            // Dimming
            gfx.DrawString(circuit.LoadClassification, fontRow, XBrushes.Black,
                new XPoint(colX[2] + ColumnPadding, baseline));

            // Fixtures
            gfx.DrawString(circuit.FixturesDisplay, fontRow, XBrushes.Black,
                new XPoint(colX[3] + ColumnPadding, baseline));

            // Quantity
            gfx.DrawString(circuit.QuantityDisplay, fontRow, XBrushes.Black,
                new XPoint(colX[4] + ColumnPadding, baseline));

            // Driver
            gfx.DrawString(circuit.DriverDisplay, fontRow, XBrushes.Black,
                new XPoint(colX[5] + ColumnPadding, baseline));

            // Wattage
            gfx.DrawString(circuit.TotalWattsDisplay, fontRow, XBrushes.Black,
                new XPoint(colX[6] + ColumnPadding, baseline));

            y += LineHeight;

            // Subtle gridline after each row
            gfx.DrawLine(gridPen, MarginLeft, y, PageWidth - MarginRight, y);
        }

        // Dispose main graphics before footer pass
        gfx?.Dispose();
        gfx = null;

        // Footer + page numbers on every page
        for (int i = 0; i < pdf.PageCount; i++)
        {
            using var g = XGraphics.FromPdfPage(pdf.Pages[i]);
            DrawFooter(g, settings, fontPageNum, i + 1, pdf.PageCount);
        }

        pdf.Save(outputPath);
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
