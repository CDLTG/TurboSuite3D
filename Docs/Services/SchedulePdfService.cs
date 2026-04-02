using System.Collections.Generic;
using System.Linq;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

public static class SchedulePdfService
{
    #region Layout Constants

    // ── Page (Large = 11x29 construction strip, Small = 8.5x11 letter) ──
    private const double LargePageWidth  = 11.0 * 72;   // 792 pt
    private const double LargePageHeight = 29.0 * 72;   // 2088 pt
    private const double SmallPageWidth  = 8.5  * 72;   // 612 pt
    private const double SmallPageHeight = 11.0 * 72;   // 792 pt

    // ── Margins ──
    private const double MarginLeft   = 36;
    private const double MarginRight  = 36;
    private const double MarginTop    = 36;
    private const double MarginBottom = 36;

    // ── Title ──
    private const double TitleFontSize = 14;
    private const double TitleSpacing  = 20;

    // ── Type Mark Box (height = 4 × LineHeight so box matches the 4 content lines) ──
    private const double TypeMarkBoxWidth  = 48;
    private const double TypeMarkFontSize  = 17.5;
    private const double TypeMarkBorderWidth = 1.2;

    // ── Content Area (right of Type Mark box) ──
    private const double ContentGap = 10;  // gap between Type Mark box and content

    // ── Text Lines ──
    private const double CatalogFontSize      = 9.5;
    private const double ManufacturerFontSize  = 9;
    private const double DescriptionFontSize   = 8;
    private const double SpecLabelFontSize     = 7.5;
    private const double SpecValueFontSize     = 7.5;
    private const double NoteFontSize          = 7.5;

    private const double LineHeight         = 12;   // standard line spacing (box = 4 × this)
    private const double BaselineOffset     = 9;   // uniform baseline within each line
    private const double NoteLineHeight     = 11;
    private const double NoteIndent         = 12;

    // ── Entry Spacing ──
    private const double EntrySpacing = 12;  // vertical gap between fixture entries

    // ── Colors ──
    private static readonly XColor DescriptionColor = XColor.FromArgb(180, 100, 45);   // warm brown/orange
    private static readonly XColor NoteColor        = XColor.FromArgb(55, 86, 135);    // blue
    private static readonly XColor SpecLabelColor   = XColor.FromGrayScale(0.40);
    private static readonly XColor SpecValueColor   = XColor.FromArgb(0, 128, 128);    // teal

    // ── Spec Grid Layout ──
    private const double SpecColumnGap = 8;   // gap between label and value
    private const double SpecSectionGap = 12; // gap between left-side text and spec col 1, and between spec col 1 values and spec col 2 labels

    #endregion

    public static void Generate(
        List<ScheduleFixtureModel> fixtures,
        string projectName,
        string outputPath,
        bool useLargeFormat)
    {
        double pageWidth  = useLargeFormat ? LargePageWidth  : SmallPageWidth;
        double pageHeight = useLargeFormat ? LargePageHeight : SmallPageHeight;
        double typeMarkBoxHeight = 4 * LineHeight;  // box matches the 4 content lines
        double contentLeft = MarginLeft + TypeMarkBoxWidth + ContentGap;
        double contentWidth = pageWidth - MarginRight - contentLeft;

        // Fonts
        var fontTitle       = new XFont("Segoe UI", TitleFontSize, XFontStyle.Bold);
        var fontTypeMark    = new XFont("Segoe UI", TypeMarkFontSize, XFontStyle.Bold);
        var fontCatalog     = new XFont("Segoe UI", CatalogFontSize, XFontStyle.Bold);
        var fontManufacturer = new XFont("Segoe UI", ManufacturerFontSize);
        var fontDesc        = new XFont("Segoe UI Light", DescriptionFontSize);
        var fontSpecLabel   = new XFont("Segoe UI", SpecLabelFontSize);
        var fontSpecValue   = new XFont("Segoe UI", SpecValueFontSize);
        var fontNote        = new XFont("Segoe UI", NoteFontSize);
        var fontPageNum     = new XFont("Segoe UI Light", 7);

        var penTypeMarkBox  = new XPen(XColors.Black, TypeMarkBorderWidth);
        var brushDesc       = new XSolidBrush(DescriptionColor);
        var brushNote       = new XSolidBrush(NoteColor);
        var brushSpecLabel  = new XSolidBrush(SpecLabelColor);
        var brushSpecValue  = new XSolidBrush(SpecValueColor);

        // ── Measurement pass: dynamically position the 3 spec sections ──
        //    Layout: [descriptions] gap [mechanical] gap [electrical] gap [photometric]
        double specLabelWidth;
        double maxLeftTextWidth = 0;       // widest manufacturer/description text
        double maxMechValueWidth = 0;      // widest mechanical value (Finish/Listings/Mounting)
        double maxElecValueWidth = 0;      // widest electrical value (Dimming/Watts/Volts)

        using (var tempPdf = new PdfDocument())
        {
            var tempPage = tempPdf.AddPage();
            using var tempGfx = XGraphics.FromPdfPage(tempPage);

            specLabelWidth = MeasureMaxSpecLabelWidth(tempGfx, fontSpecLabel);

            foreach (var f in fixtures)
            {
                // Measure left-side text (manufacturer, desc1, desc2)
                foreach (var text in new[] { f.Manufacturer, f.Description1, f.Description2 })
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var font = text == f.Manufacturer ? fontManufacturer : fontDesc;
                        double w = tempGfx.MeasureString(text, font).Width;
                        if (w > maxLeftTextWidth) maxLeftTextWidth = w;
                    }
                }

                // Measure mechanical values (Finish, Listings, Mounting)
                foreach (var val in new[] { f.Finish, f.Listings, f.Mounting })
                {
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        double w = tempGfx.MeasureString(val, fontSpecValue).Width;
                        if (w > maxMechValueWidth) maxMechValueWidth = w;
                    }
                }

                // Measure electrical values (Dimming, Watts, Volts)
                foreach (var val in new[] { f.Dimming, f.Watts, f.Volts })
                {
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        double w = tempGfx.MeasureString(val, fontSpecValue).Width;
                        if (w > maxElecValueWidth) maxElecValueWidth = w;
                    }
                }
            }
        }

        // Mechanical section starts after the widest left-side text + gap
        double mechColLeft = contentLeft + maxLeftTextWidth + SpecSectionGap;
        // Electrical section starts after mechanical label + gap + widest mechanical value + gap
        double elecColLeft = mechColLeft + specLabelWidth + SpecColumnGap + maxMechValueWidth + SpecSectionGap;
        // Photometric section starts after electrical label + gap + widest electrical value + gap
        double photoColLeft = elecColLeft + specLabelWidth + SpecColumnGap + maxElecValueWidth + SpecSectionGap;

        using var pdf = new PdfDocument();
        pdf.Info.Title = $"{projectName} Fixture Schedule";

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

            // Title on first page only
            if (pdf.PageCount == 1)
            {
                gfx.DrawString("Fixture Schedule", fontTitle, XBrushes.Black,
                    new XPoint(MarginLeft, y + TitleFontSize));
                y += TitleFontSize + TitleSpacing;
            }
        }

        StartNewPage();

        foreach (var fixture in fixtures)
        {
            double entryHeight = MeasureEntryHeight(fixture);

            // Page break — never split a fixture entry
            if (y + entryHeight > pageHeight - MarginBottom)
            {
                StartNewPage();
            }

            double entryTop = y;

            // ── Type Mark Box ──
            double boxY = entryTop;
            gfx!.DrawRectangle(penTypeMarkBox,
                MarginLeft, boxY, TypeMarkBoxWidth, typeMarkBoxHeight);

            // Center Type Mark text in box — shrink font if text is too wide
            double boxPadding = 6;
            double maxTextWidth = TypeMarkBoxWidth - boxPadding * 2;
            var tmFont = fontTypeMark;
            double measuredWidth = gfx.MeasureString(fixture.TypeMark, tmFont).Width;
            if (measuredWidth > maxTextWidth)
            {
                double scale = maxTextWidth / measuredWidth;
                tmFont = new XFont("Segoe UI", TypeMarkFontSize * scale, XFontStyle.Bold);
            }

            var typeMarkRect = new XRect(MarginLeft, boxY, TypeMarkBoxWidth, typeMarkBoxHeight);
            var typeMarkFormat = new XStringFormat
            {
                Alignment = XStringAlignment.Center,
                LineAlignment = XLineAlignment.Center
            };
            gfx.DrawString(fixture.TypeMark, tmFont, XBrushes.Black, typeMarkRect, typeMarkFormat);

            // ── Content area (right of box) ──
            double lineY = entryTop;

            // Line 1: Catalog Numbers (bold)
            if (!string.IsNullOrWhiteSpace(fixture.CatalogNumber))
            {
                gfx.DrawString(fixture.CatalogNumber, fontCatalog, XBrushes.Black,
                    new XPoint(contentLeft, lineY + BaselineOffset));
            }
            lineY += LineHeight;

            // Lines 2–4: Left-side text (fixed rows)
            double line2Y = lineY;
            if (!string.IsNullOrWhiteSpace(fixture.Manufacturer))
                gfx.DrawString(fixture.Manufacturer, fontManufacturer, XBrushes.Black,
                    new XPoint(contentLeft, line2Y + BaselineOffset));

            double line3Y = lineY + LineHeight;
            if (!string.IsNullOrWhiteSpace(fixture.Description1))
                gfx.DrawString(fixture.Description1, fontDesc, brushDesc,
                    new XPoint(contentLeft, line3Y + BaselineOffset));

            double line4Y = lineY + 2 * LineHeight;
            if (!string.IsNullOrWhiteSpace(fixture.Description2))
                gfx.DrawString(fixture.Description2, fontDesc, brushDesc,
                    new XPoint(contentLeft, line4Y + BaselineOffset));

            // Spec sections: each draws its non-empty values top-down from line 2
            var rightAlign = new XStringFormat
            {
                Alignment = XStringAlignment.Far,
                LineAlignment = XLineAlignment.BaseLine
            };

            DrawSectionStack(gfx, lineY, mechColLeft, specLabelWidth,
                [("Finish:", fixture.Finish), ("Listings:", fixture.Listings), ("Mounting:", fixture.Mounting)],
                fontSpecLabel, fontSpecValue, brushSpecLabel, brushSpecValue, rightAlign);

            DrawSectionStack(gfx, lineY, elecColLeft, specLabelWidth,
                [("Dimming:", fixture.Dimming), ("Watts:", fixture.Watts), ("Volts:", fixture.Volts)],
                fontSpecLabel, fontSpecValue, brushSpecLabel, brushSpecValue, rightAlign);

            DrawSectionStack(gfx, lineY, photoColLeft, specLabelWidth,
                [("Lumens:", fixture.Lumens), ("CCT:", fixture.CCT), ("CRI:", fixture.CRI)],
                fontSpecLabel, fontSpecValue, brushSpecLabel, brushSpecValue, rightAlign);

            lineY += 3 * LineHeight;

            // ── Schedule Notes ──
            for (int i = 0; i < fixture.ScheduleNotes.Length; i++)
            {
                string noteText = $"\u2013 {fixture.ScheduleNotes[i]}";
                gfx.DrawString(noteText, fontNote, brushNote,
                    new XPoint(contentLeft + NoteIndent, lineY + BaselineOffset));
                lineY += NoteLineHeight;
            }

            y = entryTop + entryHeight + EntrySpacing;
        }

        // Dispose main graphics before page number pass
        gfx?.Dispose();
        gfx = null;

        // Page numbers
        for (int i = 0; i < pdf.PageCount; i++)
        {
            using var g = XGraphics.FromPdfPage(pdf.Pages[i]);
            g.DrawString($"Page {i + 1} of {pdf.PageCount}", fontPageNum, XBrushes.Gray,
                new XPoint(pageWidth - MarginRight, pageHeight - MarginBottom + 12),
                XStringFormats.TopRight);
        }

        pdf.Save(outputPath);
    }

    private static double MeasureEntryHeight(ScheduleFixtureModel fixture)
    {
        // 4 content lines (catalog, manufacturer, desc1, desc2) + notes
        // Box height = 4 * LineHeight, so content lines always match the box;
        // notes extend below the box when present
        return 4 * LineHeight + fixture.ScheduleNotes.Length * NoteLineHeight;
    }

    private static double MeasureMaxSpecLabelWidth(XGraphics gfx, XFont font)
    {
        string[] labels = ["Finish:", "Listings:", "Mounting:", "Dimming:", "Watts:", "Volts:", "Lumens:", "CCT:", "CRI:"];
        double max = 0;
        foreach (var label in labels)
        {
            double w = gfx.MeasureString(label, font).Width;
            if (w > max) max = w;
        }
        return max;
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
}
