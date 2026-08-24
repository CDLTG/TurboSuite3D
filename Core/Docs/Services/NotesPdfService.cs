#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

public static class NotesPdfService
{
    // Install the PDFsharp font resolver before any XFont/XGraphics render (PDFsharp 6.x core
    // has no built-in system-font resolution). Idempotent; runs once before any static entry.
    static NotesPdfService() => PdfFontResolver.EnsureRegistered();

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
    private const double HeaderLogoHeight       = 50;
    private const double HeaderLogoRightInset   = -10;
    private const double HeaderHeight           = 50;
    private const double HeaderSpacing          = 5;

    // ── Notes ──
    private const double NoteFontSize    = 8.5;
    private const double NoteLineHeight  = 12;
    private const double NoteSpacing     = 7;
    private const double NumberWidth     = 20;
    private const double NoteInset       = 19;    // right-side indent to balance number column

    // ── Cover Page ──
    private const double CoverVerticalImageMaxWidth  = 100;
    private const double CoverVerticalImageMaxHeight = 280;
    private const double CoverVerticalImageMargin    = 36;
    private const double CoverHorizontalImageHeight  = 80;
    private const double CoverProjectNameFontSize    = 24;
    private const double CoverLocationFontSize       = 14;
    private const double CoverSubtitleFontSize       = 14;
    private const double CoverDateFontSize           = 13;
    private const double CoverProjectNumberFontSize  = 11.5;
    private const double CoverTextLineSpacing        = 5;
    private const double CoverBlockSpacing           = 15;

    // ── Footer ──
    private const double FooterHeight = 28;

    #endregion

    private const double ContentWidth = PageWidth - MarginLeft - MarginRight;
    private const double NoteTextWidth = ContentWidth - NumberWidth - NoteInset;
    private const double UsableBottom = PageHeight - FooterHeight;

    public static void Generate(
        List<string> notes,
        string projectName,
        string subtitle,
        string outputPath,
        DocsSettings settings,
        string projectNumber = "",
        bool isFixturePackage = true)
    {
        var fontHeaderProject  = new XFont("Segoe UI", HeaderProjectFontSize, XFontStyleEx.Bold);
        var fontHeaderSubtitle = new XFont("Segoe UI", HeaderSubtitleFontSize);
        var fontNote           = new XFont("Segoe UI", NoteFontSize);
        var fontNoteNumber     = new XFont("Segoe UI", NoteFontSize, XFontStyleEx.Bold);


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
                double logoY = y + (HeaderProjectFontSize + HeaderSubtitleFontSize - logoH) / 2;
                if (logo is XPdfForm pdfForm)
                    DrawScaledForm(gfx, pdfForm, logoX, logoY, logoW, logoH);
                else
                    gfx.DrawImage(logo, logoX, logoY, logoW, logoH);
            }

            y += HeaderHeight + HeaderSpacing;
        }

        // Cover page as page 1
        DrawCoverPage(pdf, projectName, settings, projectNumber, isFixturePackage);

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

                // Draw note number (right-aligned so ")" characters line up)
                string number = $"{i + 1})";
                gfx.DrawString(number, fontNoteNumber, XBrushes.Black,
                    new XRect(MarginLeft, y, NumberWidth - 4, NoteFontSize),
                    new XStringFormat { Alignment = XStringAlignment.Far, LineAlignment = XLineAlignment.Near });

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

        // Footer on notes pages only (skip cover page at index 0)
        for (int i = 1; i < pdf.PageCount; i++)
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

    private static void DrawCoverPage(PdfDocument pdf, string projectName, DocsSettings settings,
        string projectNumber, bool isFixturePackage)
    {
        var page = pdf.AddPage();
        page.Width  = XUnit.FromPoint(PageWidth);
        page.Height = XUnit.FromPoint(PageHeight);
        using var gfx = XGraphics.FromPdfPage(page);

        // ── Load vertical branding image (top-left) ──
        XImage vertImg = null;
        MemoryStream vertStream = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.CoverBrandingVerticalPath) &&
                File.Exists(settings.CoverBrandingVerticalPath))
            {
                if (settings.CoverBrandingVerticalPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    vertStream = new MemoryStream(File.ReadAllBytes(settings.CoverBrandingVerticalPath));
                    vertImg = XPdfForm.FromStream(vertStream);
                }
                else
                {
                    vertImg = XImage.FromFile(settings.CoverBrandingVerticalPath);
                }
            }
        }
        catch { /* image remains null */ }

        // ── Load horizontal branding image (bottom) ──
        XImage horizImg = null;
        MemoryStream horizStream = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.CoverBrandingHorizontalPath) &&
                File.Exists(settings.CoverBrandingHorizontalPath))
            {
                if (settings.CoverBrandingHorizontalPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    horizStream = new MemoryStream(File.ReadAllBytes(settings.CoverBrandingHorizontalPath));
                    horizImg = XPdfForm.FromStream(horizStream);
                }
                else
                {
                    horizImg = XImage.FromFile(settings.CoverBrandingHorizontalPath);
                }
            }
        }
        catch { /* image remains null */ }

        // ── Draw vertical branding image ──
        if (vertImg != null)
        {
            double imgW, imgH;
            if (vertImg is XPdfForm pdfVert)
            {
                double scale = Math.Min(CoverVerticalImageMaxWidth / pdfVert.PointWidth,
                                        CoverVerticalImageMaxHeight / pdfVert.PointHeight);
                imgW = pdfVert.PointWidth * scale;
                imgH = pdfVert.PointHeight * scale;
            }
            else
            {
                double scale = Math.Min(CoverVerticalImageMaxWidth / vertImg.PixelWidth,
                                        CoverVerticalImageMaxHeight / vertImg.PixelHeight);
                imgW = vertImg.PixelWidth * scale;
                imgH = vertImg.PixelHeight * scale;
            }
            double vx = CoverVerticalImageMargin;
            double vy = CoverVerticalImageMargin;
            if (vertImg is XPdfForm pdfForm)
                DrawScaledForm(gfx, pdfForm, vx, vy, imgW, imgH);
            else
                gfx.DrawImage(vertImg, vx, vy, imgW, imgH);
        }

        // ── Draw centered text block (slightly above vertical center) ──
        var fontProjectName  = new XFont("Segoe UI", CoverProjectNameFontSize, XFontStyleEx.Bold);
        var fontLocation     = new XFont("Segoe UI", CoverLocationFontSize);
        var fontSubtitle     = new XFont("Segoe UI", CoverSubtitleFontSize, XFontStyleEx.Bold);
        var fontDate         = new XFont("Segoe UI", CoverDateFontSize);
        var fontProjectNum   = new XFont("Segoe UI", CoverProjectNumberFontSize);

        string coverSubtitle = isFixturePackage
            ? "Lighting Fixture Specification Manual"
            : "Lighting Control System Specifications";

        // Calculate total text block height for vertical centering
        double totalTextHeight = CoverProjectNameFontSize;
        bool hasLocation = !string.IsNullOrWhiteSpace(settings.ProjectLocation);
        bool hasDate = !string.IsNullOrWhiteSpace(settings.HeaderDate);
        bool hasNumber = !string.IsNullOrWhiteSpace(projectNumber);

        if (hasLocation) totalTextHeight += CoverTextLineSpacing + CoverLocationFontSize;
        totalTextHeight += CoverBlockSpacing + CoverSubtitleFontSize; // block gap before subtitle
        if (hasDate) totalTextHeight += CoverTextLineSpacing + CoverDateFontSize;
        if (hasNumber) totalTextHeight += CoverBlockSpacing + CoverProjectNumberFontSize; // block gap before project #

        double textY = (PageHeight - totalTextHeight) / 2 - 20; // slightly above center
        double centerX = PageWidth / 2;

        gfx.DrawString(projectName, fontProjectName, XBrushes.Black,
            new XPoint(centerX, textY), XStringFormats.TopCenter);
        textY += CoverProjectNameFontSize + CoverTextLineSpacing;

        if (hasLocation)
        {
            gfx.DrawString(settings.ProjectLocation, fontLocation, XBrushes.Black,
                new XPoint(centerX, textY), XStringFormats.TopCenter);
            textY += CoverLocationFontSize + CoverTextLineSpacing;
        }

        textY += CoverBlockSpacing; // block gap before subtitle
        gfx.DrawString(coverSubtitle, fontSubtitle, XBrushes.Black,
            new XPoint(centerX, textY), XStringFormats.TopCenter);
        textY += CoverSubtitleFontSize + CoverTextLineSpacing;

        if (hasDate)
        {
            gfx.DrawString(settings.HeaderDate, fontDate, XBrushes.Black,
                new XPoint(centerX, textY), XStringFormats.TopCenter);
            textY += CoverDateFontSize + CoverTextLineSpacing;
        }

        if (hasNumber)
        {
            textY += CoverBlockSpacing; // block gap before project number
            gfx.DrawString($"Project #{projectNumber}", fontProjectNum, XBrushes.Black,
                new XPoint(centerX, textY), XStringFormats.TopCenter);
        }

        // ── Draw horizontal branding image (bottom, full width) ──
        if (horizImg != null)
        {
            double availW = PageWidth;
            double imgW, imgH;
            if (horizImg is XPdfForm pdfHoriz)
            {
                double scale = availW / pdfHoriz.PointWidth;
                imgW = availW;
                imgH = pdfHoriz.PointHeight * scale;
                if (imgH > CoverHorizontalImageHeight)
                {
                    imgH = CoverHorizontalImageHeight;
                    imgW = pdfHoriz.PointWidth * (imgH / pdfHoriz.PointHeight);
                }
            }
            else
            {
                double scale = availW / horizImg.PixelWidth;
                imgW = availW;
                imgH = (double)horizImg.PixelHeight * scale;
                if (imgH > CoverHorizontalImageHeight)
                {
                    imgH = CoverHorizontalImageHeight;
                    imgW = (double)horizImg.PixelWidth * (imgH / horizImg.PixelHeight);
                }
            }
            double hx = (PageWidth - imgW) / 2;
            double hy = PageHeight - imgH;
            if (horizImg is XPdfForm pdfForm)
                DrawScaledForm(gfx, pdfForm, hx, hy, imgW, imgH);
            else
                gfx.DrawImage(horizImg, hx, hy, imgW, imgH);
        }

        vertStream?.Dispose();
        horizStream?.Dispose();
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
