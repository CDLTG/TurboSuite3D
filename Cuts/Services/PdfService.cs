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
    private const double HeaderHeight = 66;  // header band including rule line
    private const double FooterHeight = 46;  // footer band including rule line
    private const double Margin = 36;        // 0.5 inch side margins for rule lines
    private const double ContentScale = 0.88; // gentle scale — header/footer mask any overlap

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
            // Open as XPdfForm so we can draw each page scaled
            using var formStream = new MemoryStream(pdfData);
            var form = XPdfForm.FromStream(formStream);

            // Also open for import to get page count
            using var countStream = new MemoryStream(pdfData);
            using var source = PdfReader.Open(countStream, PdfDocumentOpenMode.Import);
            int pageCount = source.PageCount;

            int bookmarkPageIndex = output.PageCount;

            for (int i = 0; i < pageCount; i++)
            {
                form.PageIndex = i;

                // Create a new page matching the source page size
                var page = output.AddPage();
                page.Width = source.Pages[i].Width;
                page.Height = source.Pages[i].Height;

                using var gfx = XGraphics.FromPdfPage(page);
                double pageWidth = page.Width.Point;
                double pageHeight = page.Height.Point;

                // Draw the source page scaled to 92%, centered in the page.
                // The header/footer white backgrounds will cleanly mask any
                // content that falls behind them.
                double sourceWidth = form.PointWidth;
                double sourceHeight = form.PointHeight;

                double drawWidth = sourceWidth * ContentScale;
                double drawHeight = sourceHeight * ContentScale;

                double drawX = (pageWidth - drawWidth) / 2;
                double drawY = HeaderHeight + (pageHeight - HeaderHeight - FooterHeight - drawHeight) / 2;

                gfx.DrawImage(form, drawX, drawY, drawWidth, drawHeight);

                // Draw header and footer on top (white backgrounds mask overlap)
                DrawHeader(gfx, page, logoImage, projectName, typeMark);
                DrawFooter(gfx, page, settings);
            }

            // Add bookmark pointing to first page of this fixture type
            if (!string.IsNullOrWhiteSpace(typeMark))
            {
                output.Outlines.Add(typeMark, output.Pages[bookmarkPageIndex]);
            }
        }

        output.Save(outputPath);
    }

    private static void DrawHeader(XGraphics gfx, PdfPage page, XImage? logo,
        string projectName, string typeMark)
    {
        double pageWidth = page.Width.Point;

        // White background for header area
        gfx.DrawRectangle(XBrushes.White, 0, 0, pageWidth, HeaderHeight);

        var fontNormal = new XFont("Segoe UI", 9);
        var fontBold = new XFont("Segoe UI", 11, XFontStyle.Bold);
        var fontTypeMark = new XFont("Segoe UI", 28, XFontStyle.Bold);
        var fontLabel = new XFont("Segoe UI", 8);

        // Left: logo — top-aligned with project name
        if (logo != null)
        {
            double logoHeight = 40;
            double logoWidth = logo.PixelWidth * (logoHeight / logo.PixelHeight);
            gfx.DrawImage(logo, Margin, 28, logoWidth, logoHeight);
        }

        // Center: project name + date
        double centerX = pageWidth / 2;
        gfx.DrawString(projectName, fontBold, XBrushes.Black,
            new XPoint(centerX, 28), XStringFormats.TopCenter);
        gfx.DrawString(DateTime.Now.ToString("MMM dd, yyyy"), fontNormal, XBrushes.Black,
            new XPoint(centerX, 43), XStringFormats.TopCenter);

        // Right: "Fixture Type:" label just above Type Mark, bottoms of date and Type Mark aligned
        double rightEdge = pageWidth - Margin;
        gfx.DrawString("Fixture Type:", fontLabel, XBrushes.Black,
            new XPoint(rightEdge, 29), XStringFormats.BaseLineRight);
        gfx.DrawString(typeMark, fontTypeMark, XBrushes.Black,
            new XPoint(rightEdge, 24), XStringFormats.TopRight);

        // Horizontal rule under header
        var pen = new XPen(XColors.Black, 0.5);
        gfx.DrawLine(pen, Margin, HeaderHeight - 2, pageWidth - Margin, HeaderHeight - 2);
    }

    private static void DrawFooter(XGraphics gfx, PdfPage page, CutsSettings settings)
    {
        double pageWidth = page.Width.Point;
        double pageHeight = page.Height.Point;
        double footerTop = pageHeight - FooterHeight;

        // White background for footer area
        gfx.DrawRectangle(XBrushes.White, 0, footerTop, pageWidth, FooterHeight);

        // Horizontal rule above footer
        var pen = new XPen(XColors.Black, 0.5);
        gfx.DrawLine(pen, Margin, footerTop + 4, pageWidth - Margin, footerTop + 4);

        var font = new XFont("Segoe UI", 9);
        double centerX = pageWidth / 2;

        // Line 1: address + phone — tight under rule
        var line1Parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.CompanyAddress))
            line1Parts.Add(settings.CompanyAddress);
        if (!string.IsNullOrWhiteSpace(settings.CompanyPhone))
            line1Parts.Add(settings.CompanyPhone);

        if (line1Parts.Count > 0)
        {
            string line1 = string.Join(" \u2022 ", line1Parts);
            gfx.DrawString(line1, font, XBrushes.Black,
                new XPoint(centerX, footerTop + 10), XStringFormats.TopCenter);
        }

        // Line 2: website — snug under address line
        if (!string.IsNullOrWhiteSpace(settings.CompanyWebsite))
        {
            gfx.DrawString(settings.CompanyWebsite, font, XBrushes.Black,
                new XPoint(centerX, footerTop + 21), XStringFormats.TopCenter);
        }
    }

    private static XImage? LoadLogo(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            return XImage.FromFile(path);
        }
        catch
        {
            return null;
        }
    }
}
