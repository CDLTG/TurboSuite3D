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

    // ── Row ──
    private const double RowFontSize    = 9;
    private const double LineHeight     = 14;
    private const double BaselineOffset = 10;

    // ── Notes ──
    private const double NoteFontSize   = 7.5;
    private const double NoteLineHeight = 11;
    private const double NoteIndent     = 12;

    // ── Entry Spacing ──
    private const double EntrySpacing = 10;

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

        var fontRow     = new XFont("Segoe UI", RowFontSize);
        var fontNote    = new XFont("Segoe UI", NoteFontSize);
        var brushNote   = new XSolidBrush(XColor.FromGrayScale(0.25));
        var fontPageNum = new XFont("Segoe UI Light", 7);

        // ── Measurement pass: determine column widths ──
        double circuitNumColWidth;
        double classificationColWidth;
        double wattsColWidth;

        using (var tempPdf = new PdfDocument())
        {
            var tempPage = tempPdf.AddPage();
            using var tempGfx = XGraphics.FromPdfPage(tempPage);

            double maxCircuitNum = 0;
            double maxClassification = 0;
            double maxWatts = 0;

            foreach (var c in circuits)
            {
                double w = tempGfx.MeasureString(c.CircuitNumber, fontRow).Width;
                if (w > maxCircuitNum) maxCircuitNum = w;

                w = tempGfx.MeasureString(c.LoadClassification, fontRow).Width;
                if (w > maxClassification) maxClassification = w;

                w = tempGfx.MeasureString(c.TotalWattsDisplay, fontRow).Width;
                if (w > maxWatts) maxWatts = w;
            }

            circuitNumColWidth = maxCircuitNum + 12;
            classificationColWidth = maxClassification + 12;
            wattsColWidth = maxWatts + 8;
        }

        // Column positions
        double col1X = MarginLeft;                                          // Circuit Number
        double col2X = col1X + circuitNumColWidth;                          // Load Name
        double col4Right = PageWidth - MarginRight;                         // Watts right edge
        double col3X = col4Right - wattsColWidth - classificationColWidth;  // Classification
        double loadNameMaxWidth = col3X - col2X - 4;                        // Load Name available width

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
                "Note: Verify all circuits and loads. Refer to panel schedules for complete electrical data.",
                fontHeaderNote, brushHeaderNote, new XPoint(MarginLeft, noteY));

            y += HeaderHeight + HeaderSpacing;
        }

        StartNewPage();

        foreach (var circuit in circuits)
        {
            double entryHeight = MeasureEntryHeight(circuit);

            if (y + entryHeight > PageHeight - MarginBottom - FooterHeight)
                StartNewPage();

            // ── Row: Circuit Number | Load Name | Classification | Watts ──
            double baseline = y + BaselineOffset;

            gfx!.DrawString(circuit.CircuitNumber, fontRow, XBrushes.Black,
                new XPoint(col1X, baseline));

            // Truncate Load Name if too wide
            string loadName = circuit.LoadName;
            if (gfx.MeasureString(loadName, fontRow).Width > loadNameMaxWidth && loadName.Length > 0)
            {
                while (loadName.Length > 1 && gfx.MeasureString(loadName + "\u2026", fontRow).Width > loadNameMaxWidth)
                    loadName = loadName[..^1];
                loadName += "\u2026";
            }
            gfx.DrawString(loadName, fontRow, XBrushes.Black,
                new XPoint(col2X, baseline));

            gfx.DrawString(circuit.LoadClassification, fontRow, XBrushes.Black,
                new XPoint(col3X, baseline));

            var rightAlign = new XStringFormat
            {
                Alignment = XStringAlignment.Far,
                LineAlignment = XLineAlignment.BaseLine
            };
            gfx.DrawString(circuit.TotalWattsDisplay, fontRow, XBrushes.Black,
                new XPoint(col4Right, baseline), rightAlign);

            y += LineHeight;

            // ── Notes: fixture groups ──
            double noteX = col1X + NoteIndent;
            foreach (var group in circuit.FixtureGroups)
            {
                string noteText;
                if (group.IsLinear)
                    noteText = $"\u2013 {group.TypeMark}-{FormatFeetInches(group.TotalLinearLengthFeet)}";
                else
                    noteText = $"\u2013 {group.TypeMark} (x{group.Quantity})";

                gfx.DrawString(noteText, fontNote, brushNote,
                    new XPoint(noteX, y + BaselineOffset - 2));
                y += NoteLineHeight;
            }

            y += EntrySpacing;
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

    private static double MeasureEntryHeight(LoadsCircuitModel circuit)
    {
        return LineHeight + circuit.FixtureGroups.Count * NoteLineHeight;
    }

    private static string FormatFeetInches(double feet)
    {
        int wholeFeet = (int)feet;
        int remainingInches = (int)Math.Round((feet - wholeFeet) * 12.0);
        if (remainingInches >= 12) { wholeFeet++; remainingInches = 0; }
        return $"{wholeFeet}'-{remainingInches}\"";
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
