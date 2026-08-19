using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using TurboSuite.Docs.Models;
using TurboSuite.Driver.Models;

namespace TurboSuite.Docs.Services;

/// <summary>
/// Formal paginated driver/sub-driver breakdown PDF — the packing users like from the
/// TurboRPS dashboard, rendered one section per RPS circuit. Each section's header is
/// driver-number centric (bold driver Switch IDs + type on the left, circuit info on the
/// right); the body is two columns — the sub-driver packing on the left and the circuit's
/// grouped fixtures on the right. A circuit is kept whole on one page whenever it fits;
/// only a circuit too tall for a single page is allowed to flow across pages. Letter portrait
/// only — the breakdown is a stacked layout, not a wide table, so the large "construction"
/// format doesn't apply.
/// </summary>
public static class RPSBreakdownPdfService
{
    // ── Page (letter portrait only) ──
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
    private const double HeaderLogoHeight       = 50;
    private const double HeaderLogoRightInset   = -10;
    private const double HeaderHeight           = 50;
    private const double HeaderSpacing          = 10;

    // ── Layout ──
    private const double CircuitHeaderHeight = 22;
    private const double SubHeaderHeight     = 15;
    private const double SegmentHeight       = 13;
    private const double FixtureTitleHeight  = 15;
    private const double FixtureHeadHeight   = 14;
    private const double FixtureRowHeight    = 13;
    private const double SectionSpacing      = 10;
    private const double TrailingGap         = 4;
    private const double SegmentIndent       = 18;
    // Fixed content-sized columns (not a page fraction): the Fixtures table is anchored right
    // after the sub-driver column so leftover whitespace lands on the far right, not the middle.
    // The inter-column gutter is simply the slack between LeftColumnWidth and the (shorter)
    // sub-driver text — no separate gap constant is needed.
    private const double LeftColumnWidth     = 230;   // fits "Sub-driver N (Driver M): 148.3W / 192W"
    private const double FixturesColumnWidth = 240;   // Qty · Type · Comments · Length

    private const double CircuitFontSize = 10;
    private const double TypeFontSize    = 9;
    private const double MetaFontSize    = 8.5;
    private const double SubFontSize     = 8.5;
    private const double SegmentFontSize = 8;
    private const double FixtureFontSize = 7.5;

    // ── Footer ──
    private const double FooterHeight = 28;

    // ── Colors ──
    private static readonly XColor CircuitBgColor = XColor.FromGrayScale(0.15);
    private static readonly XColor RuleColor      = XColor.FromGrayScale(0.80);
    private static readonly XColor FaintRuleColor = XColor.FromGrayScale(0.90);

    /// <summary>A single page-breakable line within one of a circuit's two columns.</summary>
    private sealed class FlowRow
    {
        public double Height;
        public Action<XGraphics, double> Draw = static (_, _) => { };
    }

    /// <summary>
    /// Generate a standalone driver breakdown PDF.
    /// </summary>
    public static void Generate(
        List<RPSBreakdownModel> circuits,
        string projectName,
        string outputPath,
        DocsSettings settings)
    {
        using var pdf = new PdfDocument();
        pdf.Info.Title = $"{projectName} Power Supply Breakdown";
        GeneratePages(pdf, circuits, projectName, settings);
        pdf.Save(outputPath);
    }

    /// <summary>
    /// Generate breakdown pages into an existing PdfDocument (for combined output).
    /// </summary>
    public static void GeneratePages(
        PdfDocument pdf,
        List<RPSBreakdownModel> circuits,
        string projectName,
        DocsSettings settings)
    {
        string logoPath = settings.LogoFilePath;
        double contentWidth = PageWidth - MarginLeft - MarginRight;
        double leftColWidth  = LeftColumnWidth;
        double rightColX     = MarginLeft + leftColWidth;
        double rightColWidth = FixturesColumnWidth;

        var fontHeader   = new XFont("Segoe UI", HeaderProjectFontSize, XFontStyle.Bold);
        var fontSubtitle = new XFont("Segoe UI", HeaderSubtitleFontSize);
        var fontCircuit  = new XFont("Segoe UI", CircuitFontSize, XFontStyle.Bold);
        var fontType     = new XFont("Segoe UI", TypeFontSize, XFontStyle.Bold);
        var fontMeta     = new XFont("Segoe UI", MetaFontSize);
        var fontSub      = new XFont("Segoe UI", SubFontSize, XFontStyle.Bold);
        var fontSegment  = new XFont("Segoe UI", SegmentFontSize);
        var fontFixTitle = new XFont("Segoe UI", TypeFontSize, XFontStyle.Bold);
        var fontFixHead  = new XFont("Segoe UI", FixtureFontSize, XFontStyle.Bold);
        var fontFixCell  = new XFont("Segoe UI", FixtureFontSize);
        var fontPageNum  = new XFont("Segoe UI Light", 7);

        var penRule      = new XPen(RuleColor, 0.5);
        var penFaintRule = new XPen(FaintRuleColor, 0.4);
        var metaBrush    = new XSolidBrush(XColor.FromGrayScale(0.35));

        // Load logo (letter header, same pattern as the lookup table)
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

        double pageBottom   = PageHeight - MarginBottom - FooterHeight;
        double freshBodyTop = MarginTop + HeaderHeight + HeaderSpacing;

        int firstPageIndex = pdf.PageCount;
        XGraphics? gfx = null;
        double y = 0;

        void StartNewPage()
        {
            gfx?.Dispose();
            var page = pdf.AddPage();
            page.Width  = XUnit.FromPoint(PageWidth);
            page.Height = XUnit.FromPoint(PageHeight);
            gfx = XGraphics.FromPdfPage(page);
            y = MarginTop;

            // ── Header ──
            gfx.DrawString(projectName, fontHeader, XBrushes.Black,
                new XPoint(MarginLeft, y + HeaderProjectFontSize));
            gfx.DrawString("POWER SUPPLY BREAKDOWN", fontSubtitle, XBrushes.Black,
                new XPoint(MarginLeft, y + HeaderProjectFontSize + HeaderSubtitleFontSize + 3));

            if (logo != null)
            {
                double logoH = HeaderLogoHeight;
                double logoW = logo is XPdfForm pdfLogo
                    ? pdfLogo.PointWidth * (logoH / pdfLogo.PointHeight)
                    : (double)logo.PixelWidth * (logoH / logo.PixelHeight);
                double logoX = PageWidth - MarginRight - logoW - HeaderLogoRightInset;
                double logoY = y + (HeaderProjectFontSize + HeaderSubtitleFontSize - logoH) / 2;
                if (logo is XPdfForm pdfForm)
                    DrawScaledForm(gfx, pdfForm, logoX, logoY, logoW, logoH);
                else
                    gfx.DrawImage(logo, logoX, logoY, logoW, logoH);
            }

            y = freshBodyTop;
        }

        // ── Per-circuit header bar (driver-number centric) ──
        void DrawCircuitHeaderBar(RPSBreakdownModel c, double top)
        {
            gfx!.DrawRectangle(new XSolidBrush(CircuitBgColor), MarginLeft, top, contentWidth, CircuitHeaderHeight);
            double baseline = top + CircuitHeaderHeight - 7;

            // LEFT (bold): driver number(s) + type.
            string typeText = c.DriverCount > 1 && !string.IsNullOrWhiteSpace(c.RecommendedType)
                ? $"{c.RecommendedType} ×{c.DriverCount}"
                : c.RecommendedType ?? string.Empty;
            double tx = MarginLeft + 6;
            if (!string.IsNullOrWhiteSpace(c.SwitchIds))
            {
                gfx.DrawString(c.SwitchIds, fontCircuit, XBrushes.White, new XPoint(tx, baseline));
                if (!string.IsNullOrWhiteSpace(typeText))
                {
                    double w = gfx.MeasureString(c.SwitchIds, fontCircuit).Width;
                    gfx.DrawString($"   ·   {typeText}", fontType, XBrushes.White, new XPoint(tx + w, baseline));
                }
            }
            else if (!string.IsNullOrWhiteSpace(typeText))
            {
                gfx.DrawString(typeText, fontCircuit, XBrushes.White, new XPoint(tx, baseline));
            }

            // RIGHT: circuit info.
            string circ = string.IsNullOrWhiteSpace(c.CircuitNumber) ? "(unassigned circuit)" : $"Circuit {c.CircuitNumber}";
            if (!string.IsNullOrWhiteSpace(c.LoadName))
                circ += $"  ·  {c.LoadName}";
            gfx.DrawString(circ, fontMeta, XBrushes.White,
                new XPoint(PageWidth - MarginRight - 6, baseline), XStringFormats.BaseLineRight);
        }

        // ── Build the two column streams for one circuit ──
        List<FlowRow> BuildLeftRows(RPSBreakdownModel c)
        {
            var rows = new List<FlowRow>();
            foreach (var sub in c.SubDrivers)
            {
                string header = SubDriverHeader(sub);
                rows.Add(new FlowRow
                {
                    Height = SubHeaderHeight,
                    Draw = (g, top) => g.DrawString(header, fontSub, XBrushes.Black,
                        new XPoint(MarginLeft + 4, top + SubHeaderHeight - 4))
                });
                foreach (var seg in sub.Segments)
                {
                    string line = SegmentLine(seg);
                    rows.Add(new FlowRow
                    {
                        Height = SegmentHeight,
                        Draw = (g, top) => g.DrawString(line, fontSegment, metaBrush,
                            new XPoint(MarginLeft + 4 + SegmentIndent, top + SegmentHeight - 3))
                    });
                }
            }
            return rows;
        }

        List<FlowRow> BuildRightRows(RPSBreakdownModel c)
        {
            var rows = new List<FlowRow>();
            if (c.Fixtures.Count == 0) return rows;

            // Fixture sub-columns: Qty | Type | Comments | Length (feet-inches, right-aligned).
            double qtyX  = rightColX + 4;
            double typeX = rightColX + 30;
            double descX = rightColX + 72;
            double lenRightX = rightColX + rightColWidth - 2;
            double lenColWidth = 46;
            double descWidth = lenRightX - lenColWidth - descX - 4;

            rows.Add(new FlowRow
            {
                Height = FixtureTitleHeight,
                Draw = (g, top) => g.DrawString("Fixtures", fontFixTitle, XBrushes.Black,
                    new XPoint(rightColX, top + FixtureTitleHeight - 3))
            });
            rows.Add(new FlowRow
            {
                Height = FixtureHeadHeight,
                Draw = (g, top) =>
                {
                    double b = top + FixtureHeadHeight - 4;
                    g.DrawString("Qty", fontFixHead, XBrushes.Black, new XPoint(qtyX, b));
                    g.DrawString("Type", fontFixHead, XBrushes.Black, new XPoint(typeX, b));
                    g.DrawString("Comments", fontFixHead, XBrushes.Black, new XPoint(descX, b));
                    g.DrawString("Length", fontFixHead, XBrushes.Black,
                        new XPoint(lenRightX, b), XStringFormats.BaseLineRight);
                    g.DrawLine(penRule, rightColX, top + FixtureHeadHeight,
                        rightColX + rightColWidth, top + FixtureHeadHeight);
                }
            });
            foreach (var fx in c.Fixtures)
            {
                var f = fx;
                rows.Add(new FlowRow
                {
                    Height = FixtureRowHeight,
                    Draw = (g, top) =>
                    {
                        double b = top + FixtureRowHeight - 3;
                        g.DrawString(f.Quantity.ToString(), fontFixCell, XBrushes.Black, new XPoint(qtyX, b));
                        g.DrawString(f.TypeMark ?? string.Empty, fontFixCell, XBrushes.Black, new XPoint(typeX, b));
                        DrawClipped(g, f.Comments ?? string.Empty, fontFixCell, XBrushes.Black, descX, b, descWidth);
                        if (f.LinearLength > 0.0001)
                            g.DrawString(FeetInches(f.LinearLength), fontFixCell, XBrushes.Black,
                                new XPoint(lenRightX, b), XStringFormats.BaseLineRight);
                        g.DrawLine(penFaintRule, rightColX, top + FixtureRowHeight,
                            rightColX + rightColWidth, top + FixtureRowHeight);
                    }
                });
            }
            return rows;
        }

        StartNewPage();

        foreach (var circuit in circuits)
        {
            var leftRows = BuildLeftRows(circuit);
            var rightRows = BuildRightRows(circuit);
            double bodyHeight = Math.Max(leftRows.Sum(r => r.Height), rightRows.Sum(r => r.Height));
            double needed = CircuitHeaderHeight + bodyHeight;
            double maxOnFreshPage = pageBottom - freshBodyTop;

            double remaining = pageBottom - y;
            if (needed <= maxOnFreshPage)
            {
                // Fits on a page — keep it whole. Push to a fresh page if it won't fit here.
                if (needed > remaining)
                    StartNewPage();
            }
            else
            {
                // Too tall for any single page — it must flow. Don't orphan the header bar.
                if (remaining < CircuitHeaderHeight + SubHeaderHeight + SegmentHeight)
                    StartNewPage();
            }

            DrawCircuitHeaderBar(circuit, y);
            double bodyTop = y + CircuitHeaderHeight;
            double yLeft = bodyTop, yRight = bodyTop;
            int li = 0, ri = 0;

            // Emit both columns, breaking to a new page only when a column overflows (rare —
            // only the too-tall-for-one-page circuits reach a break).
            while (true)
            {
                while (li < leftRows.Count && yLeft + leftRows[li].Height <= pageBottom)
                {
                    leftRows[li].Draw(gfx!, yLeft);
                    yLeft += leftRows[li].Height;
                    li++;
                }
                while (ri < rightRows.Count && yRight + rightRows[ri].Height <= pageBottom)
                {
                    rightRows[ri].Draw(gfx!, yRight);
                    yRight += rightRows[ri].Height;
                    ri++;
                }
                if (li >= leftRows.Count && ri >= rightRows.Count)
                    break;
                StartNewPage();
                yLeft = y;
                yRight = y;
            }

            // No trailing rule — the next circuit's dark header bar is its own separator.
            y = Math.Max(yLeft, yRight) + TrailingGap + SectionSpacing;
        }

        gfx?.Dispose();

        // Footer + page numbers
        int lastPageIndex = pdf.PageCount - 1;
        int totalPages = lastPageIndex - firstPageIndex + 1;
        for (int i = firstPageIndex; i <= lastPageIndex; i++)
        {
            using var g = XGraphics.FromPdfPage(pdf.Pages[i]);
            DrawFooter(g, settings, fontPageNum, i - firstPageIndex + 1, totalPages);
        }

        logoStream?.Dispose();
    }

    /// <summary>"Sub-driver 2 (Driver 1): 148.3W / 192W" — mirrors the TurboRPS dashboard header.</summary>
    private static string SubDriverHeader(SubDriverAssignment sub) =>
        $"Sub-driver {sub.SubDriverIndex} (Driver {sub.DriverIndex}): {sub.TotalLoad:F1}W / {sub.Capacity:F0}W";

    /// <summary>"L4 (A): 96.0W / 8' - 0"" — mirrors the TurboRPS dashboard segment line.</summary>
    private static string SegmentLine(FixtureSegment seg)
    {
        string label = seg.TypeMark ?? "";
        if (seg.IsSplit && !string.IsNullOrEmpty(seg.SplitLabel))
            label += $" ({seg.SplitLabel})";
        return seg.LinearLength <= 0.0001
            ? $"{label}: {seg.Wattage:F1}W"
            : $"{label}: {seg.Wattage:F1}W / {FeetInches(seg.LinearLength)}";
    }

    /// <summary>Feet-inches display, rounding inches and carrying 12" up to the next foot.</summary>
    private static string FeetInches(double feet)
    {
        int wholeFeet = (int)feet;
        int inches = (int)Math.Round((feet - wholeFeet) * 12.0);
        if (inches >= 12) { wholeFeet++; inches = 0; }
        return $"{wholeFeet}' - {inches}\"";
    }

    /// <summary>Draw text, shrinking the font if it would exceed the given width (matches the lookup table).</summary>
    private static void DrawClipped(XGraphics gfx, string text, XFont font, XBrush brush, double x, double baseline, double maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0) return;
        double width = gfx.MeasureString(text, font).Width;
        var f = font;
        if (width > maxWidth)
            f = new XFont(font.FontFamily.Name, font.Size * (maxWidth / width));
        gfx.DrawString(text, f, brush, new XPoint(x, baseline));
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

        // Release date left-aligned, mirroring the page number
        if (!string.IsNullOrWhiteSpace(settings.FooterDate))
            gfx.DrawString(settings.FooterDate, fontPageNum, XBrushes.Gray,
                new XPoint(MarginLeft, fTop + 10), XStringFormats.TopLeft);

        gfx.DrawString($"Page {pageNumber} of {pageCount}", fontPageNum, XBrushes.Gray,
            new XPoint(PageWidth - MarginRight, fTop + 10), XStringFormats.TopRight);
    }
}
