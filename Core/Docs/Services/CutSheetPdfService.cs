using System;
using System.Collections.Generic;
using System.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

public static class CutSheetPdfService
{
    // Install the PDFsharp font resolver before any XFont/XGraphics render (PDFsharp 6.x core
    // has no built-in system-font resolution). Idempotent; runs once before any static entry.
    static CutSheetPdfService() => PdfFontResolver.EnsureRegistered();

    private const double HeaderHeight = 79;
    private const double FooterHeight = 28;
    private const double Margin = 36;
    private const double ContentScale = 0.88;

    public static void MergeAndStamp(
        List<(string typeMark, byte[]? pdfData, string catalogNumber, string dataSheetUrl)> specSheets,
        DocsSettings settings,
        string projectName,
        string outputPath)
    {
        using var output = new PdfDocument();
        output.Info.Title = $"{projectName} Cut Sheets";

        // Logo stream must stay alive until after Save() — XPdfForm reads lazily
        LoadLogo(settings.LogoFilePath, out var logo, out var logoStream);
        string dateText = !string.IsNullOrWhiteSpace(settings.HeaderDate)
            ? settings.HeaderDate
            : DateTime.Now.ToString("MMM dd, yyyy");

        foreach (var (typeMark, pdfData, catalogNumber, dataSheetUrl) in specSheets)
        {
            int bookmarkPageIndex = output.PageCount;

            if (pdfData != null)
            {
                try
                {
                    using var formStream = new MemoryStream(pdfData);
                    var form = XPdfForm.FromStream(formStream);
                    int pageCount = form.PageCount;

                    for (int i = 0; i < pageCount; i++)
                    {
                        form.PageIndex = i;
                        var page = output.AddPage();
                        page.Width = XUnit.FromPoint(form.PointWidth);
                        page.Height = XUnit.FromPoint(form.PointHeight);

                        using var gfx = XGraphics.FromPdfPage(page);
                        double pageWidth = page.Width.Point;
                        double pageHeight = page.Height.Point;

                        double drawWidth = form.PointWidth * ContentScale;
                        double drawHeight = form.PointHeight * ContentScale;
                        double drawX = (pageWidth - drawWidth) / 2;
                        double drawY = HeaderHeight + (pageHeight - HeaderHeight - FooterHeight - drawHeight) / 2;

                        DrawScaledForm(gfx, form, drawX, drawY, drawWidth, drawHeight);
                        DrawHeader(gfx, page, logo, projectName, typeMark, dateText, catalogNumber);
                        DrawFooter(gfx, page, settings);
                    }
                }
                catch (PdfSharp.Pdf.IO.PdfReaderException)
                {
                    // Owner-password-protected PDF — render placeholder page
                    var page = output.AddPage();
                    using var gfx = XGraphics.FromPdfPage(page);
                    gfx.DrawRectangle(XBrushes.White, 0, 0, page.Width.Point, page.Height.Point);
                    DrawHeader(gfx, page, logo, projectName, typeMark, dateText, catalogNumber);
                    DrawPasswordNotice(gfx, page, dataSheetUrl);
                    DrawFooter(gfx, page, settings);
                }
            }
            else
            {
                // Blank page with header, footer, and catalog number
                var page = output.AddPage();
                using var gfx = XGraphics.FromPdfPage(page);

                gfx.DrawRectangle(XBrushes.White, 0, 0, page.Width.Point, page.Height.Point);
                DrawHeader(gfx, page, logo, projectName, typeMark, dateText, catalogNumber);
                DrawFooter(gfx, page, settings);
            }

            if (!string.IsNullOrWhiteSpace(typeMark))
                output.Outlines.Add(typeMark, output.Pages[bookmarkPageIndex]);
        }

        output.Save(outputPath);
        logoStream?.Dispose();
    }

    /// <summary>
    /// Draws an XPdfForm at the specified rectangle using a coordinate transform.
    /// DrawImage is passed the form's NATURAL dimensions and the TranslateTransform +
    /// ScaleTransform does all the sizing — so this is agnostic to whether the renderer
    /// honors width/height on DrawImage(XPdfForm,...) (PdfSharpCore ignored them; PDFsharp
    /// 6.x honors them, but here the args equal the natural size, so the result is identical).
    /// </summary>
    private static void DrawScaledForm(XGraphics gfx, XPdfForm form, double x, double y, double width, double height)
    {
        if (form.PointWidth <= 0 || form.PointHeight <= 0) return;

        var state = gfx.Save();
        gfx.TranslateTransform(x, y);
        gfx.ScaleTransform(width / form.PointWidth, height / form.PointHeight);
        gfx.DrawImage(form, 0, 0, form.PointWidth, form.PointHeight);
        gfx.Restore(state);
    }

    private static void DrawHeader(XGraphics gfx, PdfPage page, XImage? logo,
        string projectName, string typeMark, string headerDate, string catalogNumber)
    {
        double pageWidth = page.Width.Point;

        gfx.DrawRectangle(XBrushes.White, 0, 0, pageWidth, HeaderHeight);

        // Left: logo — top-aligned with project name
        double logoX = Margin;
        double logoY = 18;
        double logoHeight = 45;
        if (logo is XPdfForm pdfLogo)
        {
            double logoWidth = pdfLogo.PointWidth * (logoHeight / pdfLogo.PointHeight);
            DrawScaledForm(gfx, pdfLogo, logoX, logoY, logoWidth, logoHeight);
        }
        else if (logo != null)
        {
            double logoWidth = (double)logo.PixelWidth * (logoHeight / logo.PixelHeight);
            gfx.DrawImage(logo, logoX, logoY, logoWidth, logoHeight);
        }

        // Center: project name + date
        double centerX = pageWidth / 2;
        var fontBold = new XFont("Segoe UI", 11, XFontStyleEx.Bold);
        var fontDate = new XFont("Segoe UI Light", 9);
        var dateBrush = new XSolidBrush(XColor.FromGrayScale(0.4));

        gfx.DrawString(projectName, fontBold, XBrushes.Black,
            new XPoint(centerX, 31), XStringFormats.TopCenter);
        gfx.DrawString(headerDate, fontDate, dateBrush,
            new XPoint(centerX, 45), XStringFormats.TopCenter);

        // Right: type mark only (no label)
        double rightEdge = pageWidth - Margin;
        var fontTypeMark = new XFont("Segoe UI", 28, XFontStyleEx.Bold);
        gfx.DrawString(typeMark, fontTypeMark, XBrushes.Black,
            new XPoint(rightEdge, 24), XStringFormats.TopRight);

        var pen = new XPen(XColor.FromGrayScale(0.75), 0.25);
        gfx.DrawLine(pen, Margin, 64, pageWidth - Margin, 64);

        // Catalog number line — right-justified below first rule
        if (!string.IsNullOrWhiteSpace(catalogNumber))
        {
            gfx.DrawString(catalogNumber, fontDate, dateBrush,
                new XPoint(rightEdge, 63.6), XStringFormats.TopRight);
        }

        gfx.DrawLine(pen, Margin, HeaderHeight - 2, pageWidth - Margin, HeaderHeight - 2);
    }

    private static void DrawFooter(XGraphics gfx, PdfPage page, DocsSettings settings)
    {
        double w = page.Width.Point;
        double fTop = page.Height.Point - FooterHeight;

        gfx.DrawRectangle(XBrushes.White, 0, fTop, w, FooterHeight);

        gfx.DrawLine(new XPen(XColor.FromGrayScale(0.8), 0.25),
            Margin, fTop + 2, w - Margin, fTop + 2);

        // All info on one line, centered, em-dash separated
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
                new XPoint(w / 2, fTop + 10), XStringFormats.TopCenter);
        }
    }

    private static void DrawPasswordNotice(XGraphics gfx, PdfPage page, string dataSheetUrl)
    {
        double w = page.Width.Point;
        double h = page.Height.Point;
        double centerY = HeaderHeight + (h - HeaderHeight - FooterHeight) / 2;

        var font = new XFont("Segoe UI", 11);
        var brush = new XSolidBrush(XColor.FromGrayScale(0.45));

        gfx.DrawString("This cut sheet PDF is password-protected and cannot be embedded.",
            font, brush, new XPoint(w / 2, centerY - 10), XStringFormats.TopCenter);
        gfx.DrawString("Print this page separately from the manufacturer's website.",
            font, brush, new XPoint(w / 2, centerY + 10), XStringFormats.TopCenter);

        if (!string.IsNullOrWhiteSpace(dataSheetUrl))
        {
            var urlFont = new XFont("Segoe UI", 9);
            var urlBrush = new XSolidBrush(XColor.FromArgb(0x33, 0x66, 0xCC));
            gfx.DrawString(dataSheetUrl, urlFont, urlBrush,
                new XPoint(w / 2, centerY + 32), XStringFormats.TopCenter);
        }
    }

    /// <summary>
    /// Loads a logo from the given path. PDF logos must be flattened (no annotations/markups).
    /// The caller must keep <paramref name="stream"/> alive until after the output PDF is saved.
    /// </summary>
    private static void LoadLogo(string path, out XImage? logo, out MemoryStream? stream)
    {
        logo = null;
        stream = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                stream = new MemoryStream(File.ReadAllBytes(path));
                logo = XPdfForm.FromStream(stream);
            }
            else
            {
                logo = XImage.FromFile(path);
            }
        }
        catch
        {
            // logo remains null — header renders without it
        }
    }
}
