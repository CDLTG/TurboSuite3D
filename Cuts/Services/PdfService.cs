using System;
using System.Collections.Generic;
using System.IO;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using TurboSuite.Cuts.Models;

namespace TurboSuite.Cuts.Services;

public static class PdfService
{
    private const double HeaderHeight = 66;
    private const double FooterHeight = 28;
    private const double Margin = 36;
    private const double ContentScale = 0.89;

    public static void MergeAndStamp(
        List<(string typeMark, byte[] pdfData)> specSheets,
        CutsSettings settings,
        string projectName,
        string outputPath)
    {
        using var output = new PdfDocument();
        output.Info.Title = $"{projectName} Cut Sheets";

        var logoImage = LoadLogo(settings.LogoFilePath);

        foreach (var (typeMark, pdfData) in specSheets)
        {
            using var formStream = new MemoryStream(pdfData);
            var form = XPdfForm.FromStream(formStream);

            using var countStream = new MemoryStream(pdfData);
            using var source = PdfReader.Open(countStream, PdfDocumentOpenMode.Import);
            int pageCount = source.PageCount;
            int bookmarkPageIndex = output.PageCount;

            for (int i = 0; i < pageCount; i++)
            {
                form.PageIndex = i;
                var page = output.AddPage();
                page.Width = source.Pages[i].Width;
                page.Height = source.Pages[i].Height;

                using var gfx = XGraphics.FromPdfPage(page);
                double pageWidth = page.Width.Point;
                double pageHeight = page.Height.Point;

                double drawWidth = form.PointWidth * ContentScale;
                double drawHeight = form.PointHeight * ContentScale;
                double drawX = (pageWidth - drawWidth) / 2;
                double drawY = HeaderHeight + (pageHeight - HeaderHeight - FooterHeight - drawHeight) / 2;

                gfx.DrawImage(form, drawX, drawY, drawWidth, drawHeight);
                string dateText = !string.IsNullOrWhiteSpace(settings.HeaderDate)
                    ? settings.HeaderDate
                    : DateTime.Now.ToString("MMM dd, yyyy");
                DrawHeader(gfx, page, logoImage, projectName, typeMark, dateText);
                DrawFooter(gfx, page, settings);
            }

            if (!string.IsNullOrWhiteSpace(typeMark))
                output.Outlines.Add(typeMark, output.Pages[bookmarkPageIndex]);
        }

        output.Save(outputPath);
    }

    private static void DrawHeader(XGraphics gfx, PdfPage page, XImage? logo,
        string projectName, string typeMark, string headerDate)
    {
        double pageWidth = page.Width.Point;

        // White background
        gfx.DrawRectangle(XBrushes.White, 0, 0, pageWidth, HeaderHeight);

        // Left: logo — top-aligned with project name
        if (logo != null)
        {
            double logoHeight = 40;
            double nativeW = logo is XPdfForm pdfLogo ? pdfLogo.PointWidth : logo.PixelWidth;
            double nativeH = logo is XPdfForm pdfLogoH ? pdfLogoH.PointHeight : logo.PixelHeight;
            double logoWidth = nativeW * (logoHeight / nativeH);
            gfx.DrawImage(logo, Margin, 28, logoWidth, logoHeight);
        }

        // Center: project name + date
        double centerX = pageWidth / 2;
        var fontBold = new XFont("Segoe UI", 11, XFontStyle.Bold);
        var fontDate = new XFont("Segoe UI Light", 9);
        var dateBrush = new XSolidBrush(XColor.FromGrayScale(0.4));

        gfx.DrawString(projectName, fontBold, XBrushes.Black,
            new XPoint(centerX, 31), XStringFormats.TopCenter);
        gfx.DrawString(headerDate, fontDate, dateBrush,
            new XPoint(centerX, 45), XStringFormats.TopCenter);

        // Right: type mark only (no label)
        double rightEdge = pageWidth - Margin;
        var fontTypeMark = new XFont("Segoe UI", 28, XFontStyle.Bold);
        gfx.DrawString(typeMark, fontTypeMark, XBrushes.Black,
            new XPoint(rightEdge, 24), XStringFormats.TopRight);

        // Horizontal rule under header
        var pen = new XPen(XColor.FromGrayScale(0.75), 0.25);
        gfx.DrawLine(pen, Margin, HeaderHeight - 2, pageWidth - Margin, HeaderHeight - 2);
    }

    private static void DrawFooter(XGraphics gfx, PdfPage page, CutsSettings settings)
    {
        double w = page.Width.Point;
        double fTop = page.Height.Point - FooterHeight;

        gfx.DrawRectangle(XBrushes.White, 0, fTop, w, FooterHeight);

        // Single hairline
        gfx.DrawLine(new XPen(XColor.FromGrayScale(0.8), 0.25),
            Margin, fTop + 2, w - Margin, fTop + 2);

        // All info on one line, centered, em-dash separated
        var font = new XFont("Segoe UI Light", 7.5);
        var brush = new XSolidBrush(XColor.FromGrayScale(0.45));

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.CompanyAddress)) parts.Add(settings.CompanyAddress);
        if (!string.IsNullOrWhiteSpace(settings.CompanyPhone)) parts.Add(settings.CompanyPhone);
        if (!string.IsNullOrWhiteSpace(settings.CompanyWebsite)) parts.Add(settings.CompanyWebsite);

        if (parts.Count > 0)
        {
            gfx.DrawString(string.Join("    |    ", parts), font, brush,
                new XPoint(w / 2, fTop + 10), XStringFormats.TopCenter);
        }
    }

    private static XImage? LoadLogo(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return XPdfForm.FromFile(path);
            return XImage.FromFile(path);
        }
        catch
        {
            return null;
        }
    }
}
