#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using TurboSuite.Docs.Models;
using TurboSuite.Zones.Models;

namespace TurboSuite.Docs.Services;

public static class PanelSchedulePdfService
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
    private const double HeaderNoteFontSize     = 8;
    private const double HeaderLogoHeight       = 76;
    private const double HeaderLogoRightInset   = -18;
    private const double HeaderHeight           = 50;
    private const double HeaderSpacing          = 5;

    // ── Table ──
    private const double RowFontSize            = 9;
    private const double HeaderFontSize         = 8;
    private const double LineHeight             = 16;
    private const double ColumnHeaderHeight     = 13;
    private const double BaselineOffset         = 11;
    private const double ColumnPadding          = 6;

    // ── Panel / Module bands ──
    private const double PanelHeaderHeight      = 22;
    private const double ModuleHeaderHeight     = 18;
    private const double ModuleGap              = 8;

    // ── Footer ──
    private const double FooterHeight = 28;

    #endregion

    private const double ContentWidth = PageWidth - MarginLeft - MarginRight;
    private const double UsableBottom = PageHeight - FooterHeight;

    public static void Generate(
        PanelScheduleData data,
        string projectName,
        string outputPath,
        DocsSettings settings)
    {
        // Fonts
        var fontHeaderProject  = new XFont("Segoe UI", HeaderProjectFontSize, XFontStyle.Bold);
        var fontHeaderSubtitle = new XFont("Segoe UI", HeaderSubtitleFontSize);
        var fontHeaderNote     = new XFont("Segoe UI Light", HeaderNoteFontSize);
        var brushHeaderNote    = new XSolidBrush(XColor.FromGrayScale(0.40));

        var fontRow            = new XFont("Segoe UI", RowFontSize);
        var fontColHeader      = new XFont("Segoe UI", HeaderFontSize, XFontStyle.Bold);
        var fontPanelHeader    = new XFont("Segoe UI", 10, XFontStyle.Bold);
        var fontModuleHeader   = new XFont("Segoe UI", 9, XFontStyle.Bold);
        var fontPageNum        = new XFont("Segoe UI Light", 7);

        // Column layout for load rows (5 columns)
        const double colSlot      = 28;
        const double colCkt       = 70;
        const double colDimming   = 70;
        const double colWatts     = 70;
        double colLoad            = ContentWidth - colSlot - colCkt - colDimming - colWatts;

        double[] colW = { colSlot, colLoad, colCkt, colDimming, colWatts };
        double[] colX = new double[5];
        colX[0] = MarginLeft;
        for (int i = 1; i < 5; i++)
            colX[i] = colX[i - 1] + colW[i - 1];

        string[] colHeaders = { "#", "Load", "Ckt", "Dimming", "Watts" };

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
        pdf.Info.Title = $"{projectName} Panel Schedule";

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
            gfx.DrawString("PANEL SCHEDULE", fontHeaderSubtitle, XBrushes.Black,
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
                "Note: Verify panel schedule with official control system documentation.",
                fontHeaderNote, brushHeaderNote, new XPoint(MarginLeft, noteY));

            y += HeaderHeight + HeaderSpacing;
        }

        void DrawPanelHeader(string panelName, int panelCapacity, double panelWatts, bool isContinuation)
        {
            // Dark band
            gfx.DrawRectangle(new XSolidBrush(XColor.FromGrayScale(0.15)),
                MarginLeft, y, ContentWidth, PanelHeaderHeight);

            string partNumber = data.Brand.PanelPartNumbers.TryGetValue(panelCapacity, out var pn) ? pn : "";
            string label = isContinuation
                ? $"PANEL {panelName.ToUpperInvariant()} (continued)"
                : $"PANEL {panelName.ToUpperInvariant()}";
            if (!string.IsNullOrEmpty(partNumber))
                label += $" [{partNumber}]";

            string wattsLabel = $"Total Panel Wattage: {FormatWatts(panelWatts)}";

            var vertCenter = new XStringFormat
            {
                Alignment = XStringAlignment.Near,
                LineAlignment = XLineAlignment.Center
            };
            var vertCenterFar = new XStringFormat
            {
                Alignment = XStringAlignment.Far,
                LineAlignment = XLineAlignment.Center
            };
            double centerY = y + PanelHeaderHeight / 2;
            gfx.DrawString(label, fontPanelHeader, XBrushes.White,
                new XPoint(MarginLeft + ColumnPadding, centerY), vertCenter);
            gfx.DrawString(wattsLabel, fontPanelHeader, XBrushes.White,
                new XPoint(PageWidth - MarginRight - ColumnPadding, centerY), vertCenterFar);

            y += PanelHeaderHeight;
        }

        void DrawModuleHeader(int moduleNumber, ModuleResult module, double moduleWatts)
        {
            // Medium gray band
            gfx.DrawRectangle(new XSolidBrush(XColor.FromGrayScale(0.88)),
                MarginLeft, y, ContentWidth, ModuleHeaderHeight);

            string label = $"Module #{moduleNumber}: {module.PartNumber}";
            string wattsLabel = $"Total Wattage: {FormatWatts(moduleWatts)}";

            var vertCenter = new XStringFormat
            {
                Alignment = XStringAlignment.Near,
                LineAlignment = XLineAlignment.Center
            };
            var vertCenterFar = new XStringFormat
            {
                Alignment = XStringAlignment.Far,
                LineAlignment = XLineAlignment.Center
            };
            double centerY = y + ModuleHeaderHeight / 2;
            gfx.DrawString(label, fontModuleHeader, XBrushes.Black,
                new XPoint(MarginLeft + ColumnPadding, centerY), vertCenter);
            gfx.DrawString(wattsLabel, fontModuleHeader, XBrushes.Black,
                new XPoint(PageWidth - MarginRight - ColumnPadding, centerY), vertCenterFar);

            y += ModuleHeaderHeight;
        }

        void DrawColumnHeaders()
        {
            var headerBrush = new XSolidBrush(XColor.FromGrayScale(0.15));
            // Slot # column centered
            var centerAlign = new XStringFormat
            {
                Alignment = XStringAlignment.Center,
                LineAlignment = XLineAlignment.BaseLine
            };
            gfx.DrawString(colHeaders[0], fontColHeader, headerBrush,
                new XPoint(colX[0] + colW[0] / 2, y + BaselineOffset - 2), centerAlign);
            for (int i = 1; i < 5; i++)
            {
                gfx.DrawString(colHeaders[i], fontColHeader, headerBrush,
                    new XPoint(colX[i] + ColumnPadding, y + BaselineOffset - 2));
            }
            y += ColumnHeaderHeight;
            gfx.DrawLine(new XPen(XColor.FromGrayScale(0.85), 0.5),
                MarginLeft, y, PageWidth - MarginRight, y);
        }

        // ── Main rendering loop ──
        var allPanels = data.Allocation.Locations
            .OrderBy(l => l.LocationNumber)
            .SelectMany(l => l.Panels)
            .ToList();

        foreach (var panel in allPanels)
        {
            // Pre-calculate each module's displayed wattage, then sum for panel total.
            // This ensures the panel total matches the sum of displayed module totals
            // (avoids rounding discrepancies from summing raw values).
            var moduleWattsList = new List<double>();
            foreach (var mod in panel.Modules)
            {
                double rawModuleWatts = 0;
                foreach (var cktNum in mod.CircuitNumbers)
                {
                    if (data.CircuitLookup.TryGetValue(cktNum, out var ckt))
                        rawModuleWatts += ckt.ApparentLoadVA;
                }
                moduleWattsList.Add(rawModuleWatts);
            }
            double panelWatts = moduleWattsList.Sum(w => w < 1 ? 0 : Math.Round(w));

            // Each panel starts on a new page
            StartNewPage();
            DrawPanelHeader(panel.PanelName, panel.SelectedPanelSize, panelWatts, false);
            y += ModuleGap;

            int moduleNumber = 0;
            foreach (var module in panel.Modules)
            {
                double moduleWatts = moduleWattsList[moduleNumber];
                moduleNumber++;

                // Total rows = used slots + empty slots
                int emptySlots = Math.Max(0, module.ModuleCapacity - module.CircuitNumbers.Count);
                int totalRows = module.CircuitNumbers.Count + emptySlots;

                // Calculate module height to check page fit
                double moduleHeight = ModuleHeaderHeight + ColumnHeaderHeight
                    + (totalRows * LineHeight) + ModuleGap;

                if (y + moduleHeight > UsableBottom)
                {
                    // Module doesn't fit — start new page with continuation header
                    StartNewPage();
                    DrawPanelHeader(panel.PanelName, panel.SelectedPanelSize, panelWatts, true);
                    y += ModuleGap;
                }

                // Record top of module for the box outline
                double moduleTop = y;

                // Module header (with wattage) + column headers
                DrawModuleHeader(moduleNumber, module, moduleWatts);
                DrawColumnHeaders();

                // Load rows
                var slotCenterAlign = new XStringFormat
                {
                    Alignment = XStringAlignment.Center,
                    LineAlignment = XLineAlignment.BaseLine
                };
                int slotNumber = 0;
                foreach (var cktNum in module.CircuitNumbers)
                {
                    slotNumber++;
                    data.CircuitLookup.TryGetValue(cktNum, out var circuit);

                    double baseline = y + BaselineOffset;

                    // Slot #
                    gfx.DrawString(slotNumber.ToString(), fontRow, XBrushes.Black,
                        new XPoint(colX[0] + colW[0] / 2, baseline), slotCenterAlign);

                    // Load (truncate if needed)
                    string loadName = circuit != null
                        ? (!string.IsNullOrWhiteSpace(circuit.UpdatedLoadName)
                            ? circuit.UpdatedLoadName
                            : circuit.CurrentLoadName)
                        : "";
                    double loadMaxWidth = colW[1] - ColumnPadding * 2;
                    if (gfx.MeasureString(loadName, fontRow).Width > loadMaxWidth && loadName.Length > 0)
                    {
                        while (loadName.Length > 1 && gfx.MeasureString(loadName + "\u2026", fontRow).Width > loadMaxWidth)
                            loadName = loadName[..^1];
                        loadName += "\u2026";
                    }
                    gfx.DrawString(loadName, fontRow, XBrushes.Black,
                        new XPoint(colX[1] + ColumnPadding, baseline));

                    // Ckt
                    gfx.DrawString(cktNum, fontRow, XBrushes.Black,
                        new XPoint(colX[2] + ColumnPadding, baseline));

                    // Dimming
                    gfx.DrawString(module.DimmingType, fontRow, XBrushes.Black,
                        new XPoint(colX[3] + ColumnPadding, baseline));

                    // Watts
                    double watts = circuit?.ApparentLoadVA ?? 0;
                    gfx.DrawString(FormatWatts(watts), fontRow, XBrushes.Black,
                        new XPoint(colX[4] + ColumnPadding, baseline));

                    y += LineHeight;
                }

                // Empty slot rows
                var emptyBrush = new XSolidBrush(XColor.FromGrayScale(0.60));
                for (int e = 0; e < emptySlots; e++)
                {
                    slotNumber++;
                    double baseline = y + BaselineOffset;
                    gfx.DrawString(slotNumber.ToString(), fontRow, XBrushes.Black,
                        new XPoint(colX[0] + colW[0] / 2, baseline), slotCenterAlign);
                    gfx.DrawString("— spare —", fontRow, emptyBrush,
                        new XPoint(colX[1] + ColumnPadding, baseline));
                    y += LineHeight;
                }

                // Box outline around entire module (header + column headers + all rows)
                var boxPen = new XPen(XColor.FromGrayScale(0.50), 0.75);
                gfx.DrawRectangle(boxPen, MarginLeft, moduleTop, ContentWidth, y - moduleTop);

                y += ModuleGap;
            }
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

    private static string FormatWatts(double va)
    {
        return va < 1 ? "0 W" : $"{Math.Round(va)} W";
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
