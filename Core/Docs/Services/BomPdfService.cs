using System;
using System.Collections.Generic;
using System.IO;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using TurboSuite.Docs.Models;
using TurboSuite.Zones.Models;

namespace TurboSuite.Docs.Services;

public static class BomPdfService
{
    #region Layout Constants

    // ── Page (always letter) ──
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
    private const double HeaderNoteFontSize     = 8;
    private const double HeaderLogoHeight       = 50;
    private const double HeaderLogoRightInset   = -10;
    private const double HeaderHeight           = 50;
    private const double HeaderSpacing          = 8;

    // ── Table ──
    private const double RowFontSize       = 9;
    private const double CategoryFontSize  = 9;
    private const double LineHeight        = 16;
    private const double CategoryHeight    = 18;
    private const double CategoryGap      = 9;
    private const double BaselineOffset    = 11;
    private const double ColumnPadding     = 6;

    // ── Fixed column widths ──
    private const double ColQtyWidth = 36;

    // ── Footer ──
    private const double FooterHeight = 28;

    #endregion

    public static void Generate(
        List<BomLineItem> items,
        string projectName,
        string brandName,
        string outputPath,
        DocsSettings settings)
    {
        // Fonts
        var fontHeaderProject  = new XFont("Segoe UI", HeaderProjectFontSize, XFontStyle.Bold);
        var fontHeaderSubtitle = new XFont("Segoe UI", HeaderSubtitleFontSize);
        var fontHeaderNote     = new XFont("Segoe UI Light", HeaderNoteFontSize);
        var brushHeaderNote    = new XSolidBrush(XColor.FromGrayScale(0.40));

        var fontRow        = new XFont("Segoe UI", RowFontSize);
        var fontCategory   = new XFont("Segoe UI", CategoryFontSize, XFontStyle.Bold);
        var fontPageNum    = new XFont("Segoe UI Light", 7);

        var categoryRulePen = new XPen(XColor.FromGrayScale(0.85), 0.5);

        string subtitle = $"{brandName.ToUpperInvariant()} BILL OF MATERIALS";

        // ── Measurement pass: determine Part Number column width ──
        double colPartNumber;

        using (var tempPdf = new PdfDocument())
        {
            var tempPage = tempPdf.AddPage();
            using var tempGfx = XGraphics.FromPdfPage(tempPage);

            double maxPartNumber = 0;
            foreach (var item in items)
            {
                if (item.IsHeader) continue;
                double w = tempGfx.MeasureString(item.PartNumber ?? "", fontRow).Width;
                if (w > maxPartNumber) maxPartNumber = w;
            }

            colPartNumber = maxPartNumber + ColumnPadding * 4;
        }

        double contentWidth = PageWidth - MarginLeft - MarginRight;
        double colDescription = Math.Max(contentWidth - ColQtyWidth - colPartNumber, 100);

        // Column X positions
        double[] colX = { MarginLeft, MarginLeft + ColQtyWidth, MarginLeft + ColQtyWidth + colPartNumber };
        double[] colW = { ColQtyWidth, colPartNumber, colDescription };

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
        pdf.Info.Title = $"{projectName} Bill of Materials";

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
            gfx.DrawString(subtitle, fontHeaderSubtitle, XBrushes.Black,
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

            double noteY = y + HeaderProjectFontSize + HeaderSubtitleFontSize + 16;
            gfx.DrawString(
                "Note: Verify bill of materials with official control system documentation.",
                fontHeaderNote, brushHeaderNote, new XPoint(MarginLeft, noteY));

            y += HeaderHeight + HeaderSpacing;
        }

        StartNewPage();

        var centerAlignData = new XStringFormat
        {
            Alignment = XStringAlignment.Center,
            LineAlignment = XLineAlignment.BaseLine
        };

        var vertCenter = new XStringFormat
        {
            Alignment = XStringAlignment.Near,
            LineAlignment = XLineAlignment.Center
        };

        foreach (var item in items)
        {
            if (item.IsHeader)
            {
                // Category header — gap before, ensure it fits with at least one row after it
                y += CategoryGap;
                if (y + CategoryHeight + LineHeight > PageHeight - FooterHeight)
                    StartNewPage();

                double centerY = y + CategoryHeight / 2;
                gfx!.DrawString(item.Description, fontCategory, XBrushes.Black,
                    new XPoint(MarginLeft + ColumnPadding, centerY), vertCenter);

                y += CategoryHeight;
                gfx.DrawLine(categoryRulePen, MarginLeft, y, PageWidth - MarginRight, y);
                continue;
            }

            if (y + LineHeight > PageHeight - FooterHeight)
                StartNewPage();

            double baseline = y + BaselineOffset;

            // Qty (centered)
            if (item.Quantity > 0)
            {
                gfx!.DrawString(item.Quantity.ToString(), fontRow, XBrushes.Black,
                    new XPoint(colX[0] + colW[0] / 2, baseline), centerAlignData);
            }

            // Part Number
            gfx!.DrawString(item.PartNumber ?? "", fontRow, XBrushes.Black,
                new XPoint(colX[1] + ColumnPadding, baseline));

            // Description (truncate if too wide)
            double descMaxWidth = colW[2] - ColumnPadding * 2;
            string desc = item.Description ?? "";
            if (gfx.MeasureString(desc, fontRow).Width > descMaxWidth && desc.Length > 0)
            {
                while (desc.Length > 1 && gfx.MeasureString(desc + "\u2026", fontRow).Width > descMaxWidth)
                    desc = desc[..^1];
                desc += "\u2026";
            }
            gfx.DrawString(desc, fontRow, XBrushes.Black,
                new XPoint(colX[2] + ColumnPadding, baseline));

            y += LineHeight;
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
