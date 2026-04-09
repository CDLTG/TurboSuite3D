#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

public static class NotesPdfService
{
    #region Layout Constants

    // ── Page (letter portrait) ──
    private const double PageWidth  = 8.5 * 72;   // 612 pt
    private const double PageHeight = 11.0 * 72;   // 792 pt

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
    private const double HeaderSpacing          = 5;

    // ── Notes ──
    private const double NoteFontSize    = 8.5;
    private const double NoteLineHeight  = 12;
    private const double NoteSpacing     = 7;
    private const double NumberWidth     = 20;

    // ── Footer ──
    private const double FooterHeight = 28;

    #endregion

    private const double ContentWidth = PageWidth - MarginLeft - MarginRight;
    private const double NoteTextWidth = ContentWidth - NumberWidth;
    private const double UsableBottom = PageHeight - FooterHeight;

    public static void Generate(
        List<string> notes,
        string projectName,
        string subtitle,
        string outputPath,
        DocsSettings settings)
    {
        var fontHeaderProject  = new XFont("Segoe UI", HeaderProjectFontSize, XFontStyle.Bold);
        var fontHeaderSubtitle = new XFont("Segoe UI", HeaderSubtitleFontSize);
        var fontNote           = new XFont("Segoe UI", NoteFontSize);
        var fontNoteNumber     = new XFont("Segoe UI", NoteFontSize, XFontStyle.Bold);


        // Load logo
        XImage logo = null;
        MemoryStream logoStream = null;
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
        pdf.Info.Title = $"{projectName} {subtitle}";

        PdfPage page = null;
        XGraphics gfx = null;
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
            gfx.DrawString(subtitle.ToUpperInvariant(), fontHeaderSubtitle, XBrushes.Black,
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
        }

        // Measure and render notes
        StartNewPage();

        // Pre-measure all notes using a temp graphics context
        using (var tempPdf = new PdfDocument())
        {
            var tempPage = tempPdf.AddPage();
            using var tempGfx = XGraphics.FromPdfPage(tempPage);

            for (int i = 0; i < notes.Count; i++)
            {
                string noteText = notes[i];
                if (string.IsNullOrWhiteSpace(noteText)) continue;

                var lines = WrapText(tempGfx, noteText, fontNote, NoteTextWidth);
                double noteHeight = lines.Count * NoteLineHeight;

                // Check if this note fits on the current page
                if (y + noteHeight > UsableBottom)
                    StartNewPage();

                // Draw note number
                string number = $"{i + 1})";
                gfx.DrawString(number, fontNoteNumber, XBrushes.Black,
                    new XPoint(MarginLeft, y + NoteFontSize));

                // Draw wrapped text lines
                double textX = MarginLeft + NumberWidth;
                foreach (string line in lines)
                {
                    gfx.DrawString(line, fontNote, XBrushes.Black,
                        new XPoint(textX, y + NoteFontSize));
                    y += NoteLineHeight;
                }

                y += NoteSpacing;
            }
        }

        // Dispose main graphics before footer pass
        gfx?.Dispose();
        gfx = null;

        // Footer + page numbers on every page
        for (int i = 0; i < pdf.PageCount; i++)
        {
            using var g = XGraphics.FromPdfPage(pdf.Pages[i]);
            DrawFooter(g, settings);
        }

        pdf.Save(outputPath);
        logoStream?.Dispose();
    }

    private static List<string> WrapText(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        var lines = new List<string>();
        var words = text.Split(' ');
        string currentLine = "";

        foreach (var word in words)
        {
            string test = currentLine.Length == 0 ? word : currentLine + " " + word;
            if (gfx.MeasureString(test, font).Width <= maxWidth)
            {
                currentLine = test;
            }
            else
            {
                if (currentLine.Length > 0) lines.Add(currentLine);
                currentLine = word;
            }
        }
        if (currentLine.Length > 0) lines.Add(currentLine);
        return lines;
    }

    private static void DrawScaledForm(XGraphics gfx, XPdfForm form, double x, double y,
        double width, double height)
    {
        if (form.PointWidth <= 0 || form.PointHeight <= 0) return;
        var state = gfx.Save();
        gfx.TranslateTransform(x, y);
        gfx.ScaleTransform(width / form.PointWidth, height / form.PointHeight);
        gfx.DrawImage(form, 0, 0, form.PointWidth, form.PointHeight);
        gfx.Restore(state);
    }

    private static void DrawFooter(XGraphics gfx, DocsSettings settings)
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
    }
}
