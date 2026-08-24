using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

public static class RPSSchedulePdfService
{
    // Install the PDFsharp font resolver before any XFont/XGraphics render (PDFsharp 6.x core
    // has no built-in system-font resolution). Idempotent; runs once before any static entry.
    static RPSSchedulePdfService() => PdfFontResolver.EnsureRegistered();

    #region Layout Constants

    // ── Page (Large = 8.5x28.5 construction strip, Small = 8.5x11 letter) ──
    private const double LargePageWidth  = 8.5  * 72;   // 612 pt
    private const double LargePageHeight = 28.5 * 72;   // 2052 pt
    private const double SmallPageWidth  = 8.5  * 72;   // 612 pt
    private const double SmallPageHeight = 11.0 * 72;   // 792 pt

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
    private const double HeaderSpacing          = 5;

    // ── Type Mark Box ���─
    private const double TypeMarkBoxWidth  = 48;
    private const double TypeMarkFontSize  = 17;
    private const double TypeMarkBorderWidth = 1.2;

    // ── Content Area ──
    private const double ContentGap = 10;

    // ── Text Lines ──
    private const double CatalogFontSize      = 9.5;
    private const double ManufacturerFontSize  = 9;
    private const double DescriptionFontSize   = 8;
    private const double SpecLabelFontSize     = 7.5;
    private const double SpecValueFontSize     = 7.5;
    private const double NoteFontSize          = 7.5;

    private const double LineHeight         = 12;
    private const double BaselineOffset     = 9;
    private const double NoteLineHeight     = 11;
    private const double NoteIndent         = 12;

    // ── Entry Spacing ──
    private const double EntrySpacing = 12;

    // ── Classification Header ──
    private const double ClassHeaderFontSize = 10;
    private const double ClassHeaderHeight   = 16;
    private const double ClassHeaderSpacing  = 6;

    // ── Colors ──
    private static readonly XColor DescriptionColor = XColor.FromGrayScale(0.25);
    private static readonly XColor SpecValueColor   = XColor.FromGrayScale(0.25);
    private static readonly XColor NoteColor        = XColor.FromGrayScale(0.25);
    private static readonly XColor SpecLabelColor   = XColor.FromGrayScale(0.25);

    // ── Spec Grid Layout (2 sections: Capacity + Electrical) ��─
    private const double SpecColumnGap = 4;
    private const double SpecSectionGap = 12;

    // ── Specification Notes Block ──
    private const double SpecNotesTopSpacing = 18;

    #endregion

    private const double FooterHeight = 28;

    /// <summary>
    /// Generate a standalone RPS schedule PDF.
    /// </summary>
    public static void Generate(
        List<RPSScheduleModel> items,
        string projectName,
        string outputPath,
        bool useLargeFormat,
        DocsSettings settings)
    {
        using var pdf = new PdfDocument();
        pdf.Info.Title = $"{projectName} Power Supply Schedule";
        GeneratePages(pdf, items, projectName, useLargeFormat, settings);
        pdf.Save(outputPath);
    }

    /// <summary>
    /// Generate RPS schedule pages into an existing PdfDocument (for combined output).
    /// </summary>
    public static void GeneratePages(
        PdfDocument pdf,
        List<RPSScheduleModel> items,
        string projectName,
        bool useLargeFormat,
        DocsSettings settings)
    {
        string logoPath = settings.LogoFilePath;
        double pageWidth  = useLargeFormat ? LargePageWidth  : SmallPageWidth;
        double pageHeight = useLargeFormat ? LargePageHeight : SmallPageHeight;
        double typeMarkBoxHeight = 4 * LineHeight;
        double contentLeft = MarginLeft + TypeMarkBoxWidth + ContentGap;
        double contentWidth = pageWidth - MarginRight - contentLeft;

        // Fonts
        var fontHeaderProject  = new XFont("Segoe UI", HeaderProjectFontSize, XFontStyleEx.Bold);
        var fontHeaderSubtitle = new XFont("Segoe UI", HeaderSubtitleFontSize);
        var fontHeaderNote     = new XFont("Segoe UI Light", HeaderNoteFontSize);
        var brushHeaderNote    = new XSolidBrush(XColor.FromGrayScale(0.40));

        var fontTypeMark     = new XFont("Segoe UI", TypeMarkFontSize, XFontStyleEx.Bold);
        var fontCatalog      = new XFont("Segoe UI", CatalogFontSize, XFontStyleEx.Bold);
        var fontManufacturer = new XFont("Segoe UI", ManufacturerFontSize);
        var fontDesc         = new XFont("Segoe UI", DescriptionFontSize);
        var fontSpecLabel    = new XFont("Segoe UI", SpecLabelFontSize);
        var fontSpecValue    = new XFont("Segoe UI", SpecValueFontSize);
        var fontNote         = new XFont("Segoe UI", NoteFontSize);
        var fontPageNum      = new XFont("Segoe UI Light", 7);

        var penTypeMarkBox  = new XPen(XColors.Black, TypeMarkBorderWidth);
        var brushDesc       = new XSolidBrush(DescriptionColor);
        var brushNote       = new XSolidBrush(NoteColor);
        var brushSpecLabel  = new XSolidBrush(SpecLabelColor);
        var brushSpecValue  = new XSolidBrush(SpecValueColor);
        var fontClassHeader = new XFont("Segoe UI", ClassHeaderFontSize, XFontStyleEx.Regular);
        var penClassRule    = new XPen(XColor.FromGrayScale(0.75), 0.25);

        // ── Measurement pass: dynamically position the 2 spec sections ──
        //    Layout: [descriptions] gap [capacity] gap [electrical]
        double specLabelWidth;
        double maxLeftTextWidth = 0;
        double maxCapValueWidth = 0;    // widest capacity value (Power/Sub-Driver/Max Fixtures)
        double maxElecValueWidth = 0;   // widest electrical value (Dimming/Voltage)

        using (var tempPdf = new PdfDocument())
        {
            var tempPage = tempPdf.AddPage();
            using var tempGfx = XGraphics.FromPdfPage(tempPage);

            specLabelWidth = MeasureMaxSpecLabelWidth(tempGfx, fontSpecLabel);

            foreach (var f in items)
            {
                foreach (var text in new[] { f.Manufacturer, f.Description1, f.Description2 })
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var font = text == f.Manufacturer ? fontManufacturer : fontDesc;
                        double w = tempGfx.MeasureString(text, font).Width;
                        if (w > maxLeftTextWidth) maxLeftTextWidth = w;
                    }
                }

                // Capacity values (Power, Sub-Driver Power, Max Fixtures)
                foreach (var val in new[] { f.Power, f.SubDriverPower, f.MaxFixtures })
                {
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        double w = tempGfx.MeasureString(val, fontSpecValue).Width;
                        if (w > maxCapValueWidth) maxCapValueWidth = w;
                    }
                }

                // Electrical values (Dimming, Voltage)
                foreach (var val in new[] { f.Dimming, f.Voltage })
                {
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        double w = tempGfx.MeasureString(val, fontSpecValue).Width;
                        if (w > maxElecValueWidth) maxElecValueWidth = w;
                    }
                }
            }
        }

        // Cap left-side text width so spec sections always fit
        double totalSpecWidth = 2 * (specLabelWidth + SpecColumnGap) + maxCapValueWidth + maxElecValueWidth + SpecSectionGap;
        double maxLeftCap = contentWidth - totalSpecWidth - SpecSectionGap;
        if (maxLeftCap < 0) maxLeftCap = 0;
        if (maxLeftTextWidth > maxLeftCap)
            maxLeftTextWidth = maxLeftCap;

        // Capacity section starts after widest left-side text + gap
        double capColLeft = contentLeft + maxLeftTextWidth + SpecSectionGap;
        // Electrical section starts after capacity
        double elecColLeft = capColLeft + specLabelWidth + SpecColumnGap + maxCapValueWidth + SpecSectionGap;

        // Note wrapping measurements
        double noteX = contentLeft + NoteIndent;
        double noteMaxWidth = pageWidth - MarginRight - noteX;
        double dashWidth;
        double noteTextMaxWidth;
        using (var tempPdf2 = new PdfDocument())
        {
            var tp = tempPdf2.AddPage();
            using var tg = XGraphics.FromPdfPage(tp);
            dashWidth = tg.MeasureString("\u2013 ", fontNote).Width;
            noteTextMaxWidth = noteMaxWidth - dashWidth;
        }

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
        PdfPage? page = null;
        XGraphics? gfx = null;
        double y = 0;

        void StartNewPage()
        {
            gfx?.Dispose();
            page = pdf.AddPage();
            page.Width  = XUnit.FromPoint(pageWidth);
            page.Height = XUnit.FromPoint(pageHeight);
            gfx = XGraphics.FromPdfPage(page);
            y = MarginTop;

            // ── Header ──
            gfx.DrawString(projectName, fontHeaderProject, XBrushes.Black,
                new XPoint(MarginLeft, y + HeaderProjectFontSize));
            gfx.DrawString("POWER SUPPLY SCHEDULE", fontHeaderSubtitle, XBrushes.Black,
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
                double logoX = pageWidth - MarginRight - logoW - HeaderLogoRightInset;
                double logoY = y + (HeaderProjectFontSize + HeaderSubtitleFontSize - logoH) / 2;
                if (logo is XPdfForm pdfForm)
                    DrawScaledForm(gfx, pdfForm, logoX, logoY, logoW, logoH);
                else
                    gfx.DrawImage(logo, logoX, logoY, logoW, logoH);
            }

            double noteY = y + HeaderProjectFontSize + HeaderSubtitleFontSize + 16;
            gfx.DrawString(
                "Note: Verify all components and quantities. Refer to product specifications for complete power supply data.",
                fontHeaderNote, brushHeaderNote, new XPoint(MarginLeft, noteY));

            y += HeaderHeight + HeaderSpacing;
        }

        StartNewPage();

        // Group by classification, sorted by classification then TypeMark within
        var grouped = items
            .GroupBy(f => f.Classification ?? "")
            .OrderBy(g => string.IsNullOrWhiteSpace(g.Key) ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        var groups = new List<(string Classification, List<RPSScheduleModel> Items)>();
        foreach (var g in grouped)
            groups.Add((g.Key, g.OrderBy(f => f.TypeMark, StringComparer.OrdinalIgnoreCase).ToList()));

        foreach (var (classification, groupItems) in groups)
        {
            bool hasHeader = !string.IsNullOrWhiteSpace(classification);

            if (hasHeader)
            {
                double headerBlock = ClassHeaderHeight + ClassHeaderSpacing + MeasureEntryHeight(groupItems[0], gfx!, fontNote, noteTextMaxWidth);
                if (y + headerBlock > pageHeight - MarginBottom - FooterHeight)
                    StartNewPage();

                gfx!.DrawString(classification, fontClassHeader, XBrushes.Black,
                    new XPoint(MarginLeft, y + ClassHeaderFontSize + 2));
                gfx.DrawLine(penClassRule, MarginLeft, y + ClassHeaderHeight,
                    pageWidth - MarginRight, y + ClassHeaderHeight);
                y += ClassHeaderHeight + ClassHeaderSpacing;
            }

            foreach (var item in groupItems)
            {
                double entryHeight = MeasureEntryHeight(item, gfx!, fontNote, noteTextMaxWidth);

                if (y + entryHeight > pageHeight - MarginBottom - FooterHeight)
                    StartNewPage();

                DrawRPSEntry(gfx!, item, y, typeMarkBoxHeight, contentLeft,
                    capColLeft, elecColLeft, specLabelWidth,
                    fontTypeMark, fontCatalog, fontManufacturer, fontDesc,
                    fontSpecLabel, fontSpecValue, fontNote,
                    penTypeMarkBox, brushDesc, brushNote, brushSpecLabel, brushSpecValue,
                    pageWidth, dashWidth, noteTextMaxWidth, maxLeftTextWidth);

                y += entryHeight + EntrySpacing;
            }
        }

        // ── Specification Notes ──
        var specNotes = settings.RPSSpecificationNotes?
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        if (specNotes != null && specNotes.Count > 0)
        {
            y += SpecNotesTopSpacing;

            double prefixWidth;
            using (var tempPdf3 = new PdfDocument())
            {
                var tp = tempPdf3.AddPage();
                using var tg = XGraphics.FromPdfPage(tp);
                prefixWidth = tg.MeasureString($"{specNotes.Count}) ", fontNote).Width;
            }
            double specNoteTextMaxWidth = pageWidth - MarginRight - MarginLeft - NoteIndent - prefixWidth;

            double blockHeight = MeasureSpecNotesBlockHeight(
                gfx!, specNotes, fontNote, specNoteTextMaxWidth, prefixWidth);

            if (y + blockHeight > pageHeight - MarginBottom - FooterHeight)
                StartNewPage();

            gfx!.DrawString("Specification Notes", fontClassHeader, XBrushes.Black,
                new XPoint(MarginLeft, y + ClassHeaderFontSize + 2));
            y += ClassHeaderHeight + ClassHeaderSpacing;

            double noteIndentX = MarginLeft + NoteIndent;
            for (int i = 0; i < specNotes.Count; i++)
            {
                string prefix = $"{i + 1}) ";
                double textX = noteIndentX + prefixWidth;
                var wrapped = WrapText(gfx, specNotes[i], fontNote, specNoteTextMaxWidth);

                gfx.DrawString(prefix + wrapped[0], fontNote, brushNote,
                    new XPoint(noteIndentX, y + BaselineOffset));
                y += NoteLineHeight;

                for (int w = 1; w < wrapped.Count; w++)
                {
                    gfx.DrawString(wrapped[w], fontNote, brushNote,
                        new XPoint(textX, y + BaselineOffset));
                    y += NoteLineHeight;
                }
            }
        }

        gfx?.Dispose();
        gfx = null;

        // Footer + page numbers on the pages we added.
        // The 8.5x28.5 construction strip drops the footer entirely.
        if (!useLargeFormat)
        {
            int lastPageIndex = pdf.PageCount - 1;
            int totalPages = lastPageIndex - firstPageIndex + 1;
            for (int i = firstPageIndex; i <= lastPageIndex; i++)
            {
                using var g = XGraphics.FromPdfPage(pdf.Pages[i]);
                DrawFooter(g, pageWidth, pageHeight, settings, fontPageNum, i - firstPageIndex + 1, totalPages);
            }
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

    private static void DrawRPSEntry(XGraphics gfx, RPSScheduleModel item,
        double entryTop, double typeMarkBoxHeight, double contentLeft,
        double capColLeft, double elecColLeft, double specLabelWidth,
        XFont fontTypeMark, XFont fontCatalog, XFont fontManufacturer, XFont fontDesc,
        XFont fontSpecLabel, XFont fontSpecValue, XFont fontNote,
        XPen penTypeMarkBox, XBrush brushDesc, XBrush brushNote,
        XBrush brushSpecLabel, XBrush brushSpecValue,
        double pageWidth, double dashWidth, double noteTextMaxWidth,
        double maxLeftTextWidth)
    {
        // ── Type Mark Box ──
        gfx.DrawRectangle(penTypeMarkBox,
            MarginLeft, entryTop, TypeMarkBoxWidth, typeMarkBoxHeight);

        double boxPadding = 6;
        double maxTextWidth = TypeMarkBoxWidth - boxPadding * 2;
        var tmFont = fontTypeMark;
        double measuredWidth = gfx.MeasureString(item.TypeMark, tmFont).Width;
        if (measuredWidth > maxTextWidth)
        {
            double scale = maxTextWidth / measuredWidth;
            tmFont = new XFont("Segoe UI", TypeMarkFontSize * scale, XFontStyleEx.Bold);
        }

        var typeMarkRect = new XRect(MarginLeft, entryTop, TypeMarkBoxWidth, typeMarkBoxHeight);
        var typeMarkFormat = new XStringFormat
        {
            Alignment = XStringAlignment.Center,
            LineAlignment = XLineAlignment.Center
        };
        gfx.DrawString(item.TypeMark, tmFont, XBrushes.Black, typeMarkRect, typeMarkFormat);

        // ── Content area ──
        double lineY = entryTop;

        // Line 1: Catalog Numbers (bold)
        if (!string.IsNullOrWhiteSpace(item.CatalogNumber))
        {
            double catalogMaxWidth = pageWidth - MarginRight - contentLeft;
            var catFont = fontCatalog;
            double catWidth = gfx.MeasureString(item.CatalogNumber, catFont).Width;
            if (catWidth > catalogMaxWidth)
            {
                double scale = catalogMaxWidth / catWidth;
                catFont = new XFont("Segoe UI", CatalogFontSize * scale, XFontStyleEx.Bold);
            }
            gfx.DrawString(item.CatalogNumber, catFont, XBrushes.Black,
                new XPoint(contentLeft, lineY + BaselineOffset));
        }
        lineY += LineHeight;

        // Lines 2–4: Left-side text
        if (!string.IsNullOrWhiteSpace(item.Manufacturer))
        {
            var mfgFont = fontManufacturer;
            double mfgWidth = gfx.MeasureString(item.Manufacturer, mfgFont).Width;
            if (mfgWidth > maxLeftTextWidth)
                mfgFont = new XFont("Segoe UI", ManufacturerFontSize * (maxLeftTextWidth / mfgWidth));
            gfx.DrawString(item.Manufacturer, mfgFont, XBrushes.Black,
                new XPoint(contentLeft, lineY + BaselineOffset));
        }

        if (!string.IsNullOrWhiteSpace(item.Description1))
        {
            var d1Font = fontDesc;
            double d1Width = gfx.MeasureString(item.Description1, d1Font).Width;
            if (d1Width > maxLeftTextWidth)
                d1Font = new XFont("Segoe UI", DescriptionFontSize * (maxLeftTextWidth / d1Width));
            gfx.DrawString(item.Description1, d1Font, brushDesc,
                new XPoint(contentLeft, lineY + LineHeight + BaselineOffset));
        }

        if (!string.IsNullOrWhiteSpace(item.Description2))
        {
            var d2Font = fontDesc;
            double d2Width = gfx.MeasureString(item.Description2, d2Font).Width;
            if (d2Width > maxLeftTextWidth)
                d2Font = new XFont("Segoe UI", DescriptionFontSize * (maxLeftTextWidth / d2Width));
            gfx.DrawString(item.Description2, d2Font, brushDesc,
                new XPoint(contentLeft, lineY + 2 * LineHeight + BaselineOffset));
        }

        // Spec sections: Capacity (left), then Electrical (right)
        var rightAlign = new XStringFormat
        {
            Alignment = XStringAlignment.Far,
            LineAlignment = XLineAlignment.BaseLine
        };

        DrawSectionStack(gfx, lineY, capColLeft, specLabelWidth,
            [("Power:", item.Power), ("Sub-Driver:", item.SubDriverPower), ("Max Fixtures:", item.MaxFixtures)],
            fontSpecLabel, fontSpecValue, brushSpecLabel, brushSpecValue, rightAlign);

        DrawSectionStack(gfx, lineY, elecColLeft, specLabelWidth,
            [("Dimming:", item.Dimming), ("Voltage:", item.Voltage)],
            fontSpecLabel, fontSpecValue, brushSpecLabel, brushSpecValue, rightAlign);

        lineY += 3 * LineHeight;

        // ── Schedule Notes ──
        double noteXPos = contentLeft + NoteIndent;

        for (int i = 0; i < item.ScheduleNotes.Length; i++)
        {
            var wrappedLines = WrapText(gfx, item.ScheduleNotes[i], fontNote, noteTextMaxWidth);
            gfx.DrawString($"\u2013 {wrappedLines[0]}", fontNote, brushNote,
                new XPoint(noteXPos, lineY + BaselineOffset));
            lineY += NoteLineHeight;
            for (int w = 1; w < wrappedLines.Count; w++)
            {
                gfx.DrawString(wrappedLines[w], fontNote, brushNote,
                    new XPoint(noteXPos + dashWidth, lineY + BaselineOffset));
                lineY += NoteLineHeight;
            }
        }
    }

    private static double MeasureEntryHeight(RPSScheduleModel item,
        XGraphics gfx = null!, XFont fontNote = null!, double noteTextMaxWidth = 0)
    {
        double noteHeight = 0;
        if (gfx != null && fontNote != null)
        {
            foreach (var note in item.ScheduleNotes)
            {
                var wrappedLines = WrapText(gfx, note, fontNote, noteTextMaxWidth);
                noteHeight += wrappedLines.Count * NoteLineHeight;
            }
        }
        else
        {
            noteHeight = item.ScheduleNotes.Length * NoteLineHeight;
        }
        return 4 * LineHeight + noteHeight;
    }

    private static double MeasureSpecNotesBlockHeight(
        XGraphics gfx, List<string> notes, XFont font, double textMaxWidth, double prefixWidth)
    {
        double height = ClassHeaderHeight + ClassHeaderSpacing;
        foreach (var note in notes)
        {
            var wrapped = WrapText(gfx, note, font, textMaxWidth);
            height += wrapped.Count * NoteLineHeight;
        }
        return height;
    }

    private static double MeasureMaxSpecLabelWidth(XGraphics gfx, XFont font)
    {
        string[] labels = ["Power:", "Sub-Driver:", "Max Fixtures:", "Dimming:", "Voltage:"];
        double max = 0;
        foreach (var label in labels)
        {
            double w = gfx.MeasureString(label, font).Width;
            if (w > max) max = w;
        }
        return max;
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
        if (lines.Count == 0) lines.Add("");
        return lines;
    }

    private static void DrawSectionStack(XGraphics gfx, double startLineY,
        double colLeft, double labelWidth,
        (string Label, string Value)[] items,
        XFont labelFont, XFont valueFont, XBrush labelBrush, XBrush valueBrush,
        XStringFormat rightAlign)
    {
        int row = 0;
        foreach (var (label, value) in items)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            double baseline = startLineY + row * LineHeight + BaselineOffset;
            gfx.DrawString(label, labelFont, labelBrush,
                new XPoint(colLeft + labelWidth, baseline), rightAlign);
            gfx.DrawString(value, valueFont, valueBrush,
                new XPoint(colLeft + labelWidth + SpecColumnGap, baseline));
            row++;
        }
    }

    private static void DrawFooter(XGraphics gfx, double pageWidth, double pageHeight,
        DocsSettings settings, XFont fontPageNum, int pageNumber, int pageCount)
    {
        double fTop = pageHeight - FooterHeight;

        gfx.DrawLine(new XPen(XColor.FromGrayScale(0.8), 0.25),
            MarginLeft, fTop + 2, pageWidth - MarginLeft, fTop + 2);

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
                new XPoint(pageWidth / 2, fTop + 10), XStringFormats.TopCenter);
        }

        // Release date left-aligned, mirroring the page number
        if (!string.IsNullOrWhiteSpace(settings.FooterDate))
            gfx.DrawString(settings.FooterDate, fontPageNum, XBrushes.Gray,
                new XPoint(MarginLeft, fTop + 10), XStringFormats.TopLeft);

        gfx.DrawString($"Page {pageNumber} of {pageCount}", fontPageNum, XBrushes.Gray,
            new XPoint(pageWidth - MarginRight, fTop + 10), XStringFormats.TopRight);
    }
}
