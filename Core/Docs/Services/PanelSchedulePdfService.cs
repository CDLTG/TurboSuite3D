#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using TurboSuite.Docs.Models;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;

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
    private const double HeaderLogoHeight       = 50;
    private const double HeaderLogoRightInset   = -10;
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
        var fontRowBold        = new XFont("Segoe UI", RowFontSize, XFontStyle.Bold);
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

        // Two-level PDF outline tree: a "Location {n}" parent node, with each of that location's panels
        // ("Panel {name}") nested beneath it — mirroring TurboZones' Panel Breakdown grouping. Both are
        // set just before a panel's FIRST page and consumed by the next StartNewPage; continuation pages
        // (which also call StartNewPage) leave both null, so each parent/child appears exactly once, on its
        // opening page. pendingLocationBookmark is non-null only on a location's first panel — where a new
        // parent is created (pointing at that same first page) and remembered for the panels that follow.
        string pendingLocationBookmark = null;
        string pendingPanelBookmark = null;
        PdfOutline currentLocationOutline = null;

        void StartNewPage()
        {
            gfx?.Dispose();
            page = pdf.AddPage();
            page.Width  = XUnit.FromPoint(PageWidth);
            page.Height = XUnit.FromPoint(PageHeight);
            gfx = XGraphics.FromPdfPage(page);
            y = MarginTop;

            if (pendingLocationBookmark != null)
            {
                // opened: true so viewers render the location node expanded, its panels visible by default.
                currentLocationOutline = pdf.Outlines.Add(pendingLocationBookmark, page, true);
                pendingLocationBookmark = null;
            }
            if (pendingPanelBookmark != null)
            {
                var parent = currentLocationOutline?.Outlines ?? pdf.Outlines;
                parent.Add(pendingPanelBookmark, page);
                pendingPanelBookmark = null;
            }

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
                double logoY = y + (HeaderProjectFontSize + HeaderSubtitleFontSize - logoH) / 2;
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
            // A subsystem-placed module (DALI DIN) carries no panel wattage — its loads are DALI addresses on
            // a bus, not dimming circuits — so a dash reads truer than "0 W".
            // Trailing space is a deliberate right-margin buffer: the label is Far-aligned to the panel
            // edge, so the space becomes breathing room between the dash and the border.
            string wattsLabel = module.OrderedBySubsystem
                ? "Total Wattage: — "
                : $"Total Wattage: {FormatWatts(moduleWatts)}";

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

        // LV-compartment block: the interfaces the designer sited in this panel's low-voltage compartment
        // (Processor / QSE-IO / QSE-CI-DMX), each as part number + description. One panel per page, so the
        // page IS the location — no zone column, and none is derivable anyway (a processor serves the whole
        // job, not its panel's zone). Renders nothing when the compartment is Empty.
        void DrawLvCompartment(PanelResult panel, double panelWatts)
        {
            var devices = new List<(string PartNo, string Description)>();
            foreach (string name in panel.CompartmentSlots)
            {
                if (string.IsNullOrWhiteSpace(name)
                    || string.Equals(name, "Empty", StringComparison.OrdinalIgnoreCase))
                    continue;
                string partNo = panel.SpecialDevicePartNumbers != null
                    && panel.SpecialDevicePartNumbers.TryGetValue(name, out var pn) ? pn : name;
                devices.Add((partNo, data.Brand.GetPartDescription(partNo)));
            }
            if (devices.Count == 0) return;

            double blockHeight = ModuleHeaderHeight + ColumnHeaderHeight
                + (devices.Count * LineHeight) + ModuleGap;
            if (y + blockHeight > UsableBottom)
            {
                StartNewPage();
                DrawPanelHeader(panel.PanelName, panel.SelectedPanelSize, panelWatts, true);
                y += ModuleGap;
            }

            double blockTop = y;

            // Title band — matches a module header band.
            gfx.DrawRectangle(new XSolidBrush(XColor.FromGrayScale(0.88)),
                MarginLeft, y, ContentWidth, ModuleHeaderHeight);
            gfx.DrawString("LV COMPARTMENT", fontModuleHeader, XBrushes.Black,
                new XPoint(MarginLeft + ColumnPadding, y + ModuleHeaderHeight / 2),
                new XStringFormat { Alignment = XStringAlignment.Near, LineAlignment = XLineAlignment.Center });
            y += ModuleHeaderHeight;

            // Column headers — same weight/brush as the module table.
            const double lvPartW = 110;
            double lvDescX = MarginLeft + lvPartW;
            var lvHeaderBrush = new XSolidBrush(XColor.FromGrayScale(0.15));
            gfx.DrawString("Part No.", fontColHeader, lvHeaderBrush,
                new XPoint(MarginLeft + ColumnPadding, y + BaselineOffset - 2));
            gfx.DrawString("Description", fontColHeader, lvHeaderBrush,
                new XPoint(lvDescX + ColumnPadding, y + BaselineOffset - 2));
            y += ColumnHeaderHeight;
            gfx.DrawLine(new XPen(XColor.FromGrayScale(0.85), 0.5),
                MarginLeft, y, PageWidth - MarginRight, y);

            // Rows — one interface per line; description truncates to its column like a load name.
            foreach (var (partNo, description) in devices)
            {
                double baseline = y + BaselineOffset;
                gfx.DrawString(partNo, fontRow, XBrushes.Black,
                    new XPoint(MarginLeft + ColumnPadding, baseline));

                string desc = description ?? "";
                double descMax = ContentWidth - lvPartW - ColumnPadding * 2;
                if (gfx.MeasureString(desc, fontRow).Width > descMax && desc.Length > 0)
                {
                    while (desc.Length > 1 && gfx.MeasureString(desc + "…", fontRow).Width > descMax)
                        desc = desc[..^1];
                    desc += "…";
                }
                gfx.DrawString(desc, fontRow, XBrushes.Black,
                    new XPoint(lvDescX + ColumnPadding, baseline));
                y += LineHeight;
            }

            gfx.DrawRectangle(new XPen(XColor.FromGrayScale(0.50), 0.75),
                MarginLeft, blockTop, ContentWidth, y - blockTop);
            y += ModuleGap;
        }

        // A shade (Sivoia QS) panel page: the same dark header + module-style block as a lighting panel,
        // but a fixed ten-output QSPS-10PNL with no dimming — so the table is # | Load | Ckt (no
        // Dimming/Watts), one row per shade output padded with spares to ten. A shade panel is always
        // ≤10 rows, so it never needs a continuation page.
        void DrawShadePanel(ShadePanelResult sp)
        {
            // Shade columns: # | Load | Ckt. Deliberately mirror the lighting table's #/Load/Ckt geometry
            // (colSlot 28, colLoad = ContentWidth − 28 − 70 − 70 − 70) so the shade Ckt column lines up
            // with the lighting panels' Ckt column. A lighting panel's trailing Dimming+Watts columns
            // become white space here — acceptable, a QS motor has neither.
            const double sColSlot = 28;
            double sColLoad = ContentWidth - sColSlot - 70 - 70 - 70;
            double sxSlot = MarginLeft;
            double sxLoad = sxSlot + sColSlot;
            double sxCkt  = sxLoad + sColLoad;

            var nearCenter = new XStringFormat
            { Alignment = XStringAlignment.Near, LineAlignment = XLineAlignment.Center };
            var farCenter = new XStringFormat
            { Alignment = XStringAlignment.Far, LineAlignment = XLineAlignment.Center };
            var slotCenter = new XStringFormat
            { Alignment = XStringAlignment.Center, LineAlignment = XLineAlignment.BaseLine };

            pendingPanelBookmark = $"Panel {sp.PanelName.ToUpperInvariant()}";
            StartNewPage();

            // Dark panel header — PANEL 1-D [QSPS-10PNL]. No wattage: a QS motor is not a VA dimming load.
            gfx.DrawRectangle(new XSolidBrush(XColor.FromGrayScale(0.15)),
                MarginLeft, y, ContentWidth, PanelHeaderHeight);
            double hCenterY = y + PanelHeaderHeight / 2;
            gfx.DrawString($"PANEL {sp.PanelName.ToUpperInvariant()} [{ShadeSolver.PanelPartNumber}]",
                fontPanelHeader, XBrushes.White, new XPoint(MarginLeft + ColumnPadding, hCenterY), nearCenter);
            // Right side of the panel header stays empty — a shade panel has no wattage to show there.
            y += PanelHeaderHeight;
            y += ModuleGap;

            double blockTop = y;

            // Sub-header band — the panel's fill (n / 10 used), matching a lighting module header.
            gfx.DrawRectangle(new XSolidBrush(XColor.FromGrayScale(0.88)),
                MarginLeft, y, ContentWidth, ModuleHeaderHeight);
            double mCenterY = y + ModuleHeaderHeight / 2;
            gfx.DrawString("Shade Outputs", fontModuleHeader, XBrushes.Black,
                new XPoint(MarginLeft + ColumnPadding, mCenterY), nearCenter);
            gfx.DrawString($"{sp.ShadeCount} / {sp.Capacity} used", fontModuleHeader, XBrushes.Black,
                new XPoint(PageWidth - MarginRight - ColumnPadding, mCenterY), farCenter);
            y += ModuleHeaderHeight;

            // Column headers: # | Load | Ckt.
            var headerBrush = new XSolidBrush(XColor.FromGrayScale(0.15));
            gfx.DrawString("#", fontColHeader, headerBrush,
                new XPoint(sxSlot + sColSlot / 2, y + BaselineOffset - 2),
                new XStringFormat { Alignment = XStringAlignment.Center, LineAlignment = XLineAlignment.BaseLine });
            gfx.DrawString("Load", fontColHeader, headerBrush,
                new XPoint(sxLoad + ColumnPadding, y + BaselineOffset - 2));
            gfx.DrawString("Ckt", fontColHeader, headerBrush,
                new XPoint(sxCkt + ColumnPadding, y + BaselineOffset - 2));
            y += ColumnHeaderHeight;
            gfx.DrawLine(new XPen(XColor.FromGrayScale(0.85), 0.5),
                MarginLeft, y, PageWidth - MarginRight, y);

            // One row per shade output, then spare rows padding to the ten-output capacity.
            int outputNum = 0;
            foreach (var row in sp.Outputs)
            {
                outputNum++;
                double baseline = y + BaselineOffset;

                gfx.DrawString(outputNum.ToString(), fontRow, XBrushes.Black,
                    new XPoint(sxSlot + sColSlot / 2, baseline), slotCenter);

                string load = row.LoadName ?? "";
                double loadMax = sColLoad - ColumnPadding * 2;
                if (gfx.MeasureString(load, fontRow).Width > loadMax && load.Length > 0)
                {
                    while (load.Length > 1 && gfx.MeasureString(load + "…", fontRow).Width > loadMax)
                        load = load[..^1];
                    load += "…";
                }
                gfx.DrawString(load, fontRow, XBrushes.Black,
                    new XPoint(sxLoad + ColumnPadding, baseline));

                gfx.DrawString(row.CircuitNumber ?? "", fontRow, XBrushes.Black,
                    new XPoint(sxCkt + ColumnPadding, baseline));

                y += LineHeight;
            }

            var spareBrush = new XSolidBrush(XColor.FromGrayScale(0.60));
            for (; outputNum < sp.Capacity; )
            {
                outputNum++;
                double baseline = y + BaselineOffset;
                gfx.DrawString(outputNum.ToString(), fontRow, XBrushes.Black,
                    new XPoint(sxSlot + sColSlot / 2, baseline), slotCenter);
                gfx.DrawString("— spare —", fontRow, spareBrush,
                    new XPoint(sxLoad + ColumnPadding, baseline));
                y += LineHeight;
            }

            gfx.DrawRectangle(new XPen(XColor.FromGrayScale(0.50), 0.75),
                MarginLeft, blockTop, ContentWidth, y - blockTop);
            y += ModuleGap;
        }

        // ── Main rendering loop ──
        // Page-ordered render items: within each location its lighting panels, then its shade panels
        // (…1-C lighting, then 1-D, 1-E shades), locations in number order. Each item is its own page.
        // StartsLocation flags the first render item of each location (lighting panels, then shade panels),
        // where the "Location {n}" parent bookmark is opened; the rest nest under it.
        var renderItems = new List<(int LocationNumber, bool StartsLocation, object Item)>();
        foreach (var location in data.Allocation.Locations.OrderBy(l => l.LocationNumber))
        {
            bool firstInLocation = true;
            foreach (var panel in location.Panels)
            {
                renderItems.Add((location.LocationNumber, firstInLocation, panel));
                firstInLocation = false;
            }
            foreach (var shade in location.ShadePanels)
            {
                renderItems.Add((location.LocationNumber, firstInLocation, shade));
                firstInLocation = false;
            }
        }

        foreach (var (locationNumber, startsLocation, item) in renderItems)
        {
            if (startsLocation)
                pendingLocationBookmark = $"Location {locationNumber}";

            if (item is ShadePanelResult shadePanel)
            {
                DrawShadePanel(shadePanel);
                continue;
            }
            var panel = (PanelResult)item;
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
            pendingPanelBookmark = $"Panel {panel.PanelName.ToUpperInvariant()}";
            StartNewPage();
            DrawPanelHeader(panel.PanelName, panel.SelectedPanelSize, panelWatts, false);
            y += ModuleGap;

            // The panel's low-voltage compartment interfaces (Processor / QSE-IO / QSE-CI-DMX) lead the
            // page, above the dimming modules. Nothing drawn when the compartment is Empty.
            DrawLvCompartment(panel, panelWatts);

            int moduleNumber = 0;
            foreach (var module in panel.Modules)
            {
                double moduleWatts = moduleWattsList[moduleNumber];
                moduleNumber++;

                // DALI (any subsystem-placed) module: its slot is a loop, not a circuit list, and its
                // ModuleCapacity is the 64-load bus cap — NOT panel slots to pad with spares. Render the
                // standard header + columns, then a single loop row (Load = "Loop 1 (33/64)"), no spares.
                if (module.OrderedBySubsystem)
                {
                    double daliHeight = ModuleHeaderHeight + ColumnHeaderHeight + LineHeight + ModuleGap;
                    if (y + daliHeight > UsableBottom)
                    {
                        StartNewPage();
                        DrawPanelHeader(panel.PanelName, panel.SelectedPanelSize, panelWatts, true);
                        y += ModuleGap;
                    }

                    double daliTop = y;
                    DrawModuleHeader(moduleNumber, module, moduleWatts);
                    DrawColumnHeaders();

                    var daliCenter = new XStringFormat
                    {
                        Alignment = XStringAlignment.Center,
                        LineAlignment = XLineAlignment.BaseLine
                    };
                    double daliBaseline = y + BaselineOffset;

                    // Over the 64-load bus cap highlights red, mirroring TurboDALI's over-cap warning.
                    if (module.IsOverloaded)
                    {
                        var overloadFill = new XSolidBrush(XColor.FromArgb(255, 250, 210, 210));
                        gfx.DrawRectangle(overloadFill, MarginLeft, y, ContentWidth, LineHeight);
                    }
                    var daliBrush = module.IsOverloaded ? XBrushes.Red : XBrushes.Black;
                    var daliFont = module.IsOverloaded ? fontRowBold : fontRow;

                    // Slot #
                    gfx.DrawString("1", daliFont, daliBrush,
                        new XPoint(colX[0] + colW[0] / 2, daliBaseline), daliCenter);

                    // Load = loop + its bus load count, truncated to the column like a normal load name.
                    string daliLoad = $"{module.CircuitNumbersDisplay} ({module.UsedSlots}/{module.ModuleCapacity})";
                    double daliLoadMax = colW[1] - ColumnPadding * 2;
                    if (gfx.MeasureString(daliLoad, daliFont).Width > daliLoadMax && daliLoad.Length > 0)
                    {
                        while (daliLoad.Length > 1
                               && gfx.MeasureString(daliLoad + "…", daliFont).Width > daliLoadMax)
                            daliLoad = daliLoad[..^1];
                        daliLoad += "…";
                    }
                    gfx.DrawString(daliLoad, daliFont, daliBrush,
                        new XPoint(colX[1] + ColumnPadding, daliBaseline));

                    // Ckt — a loop has no panel circuit number.
                    gfx.DrawString("—", daliFont, daliBrush,
                        new XPoint(colX[2] + ColumnPadding, daliBaseline));

                    // Dimming
                    gfx.DrawString(module.DimmingType ?? "", daliFont, daliBrush,
                        new XPoint(colX[3] + ColumnPadding, daliBaseline));

                    // Watts — no panel wattage.
                    gfx.DrawString("—", daliFont, daliBrush,
                        new XPoint(colX[4] + ColumnPadding, daliBaseline));

                    y += LineHeight;

                    gfx.DrawRectangle(new XPen(XColor.FromGrayScale(0.50), 0.75),
                        MarginLeft, daliTop, ContentWidth, y - daliTop);

                    y += ModuleGap;
                    continue;
                }

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

                    // Highlight the whole row when this slot is over its amp limit
                    double rowWatts = circuit?.ApparentLoadVA ?? 0;
                    var rowLimits = data.Brand.GetAmpLimits(module.PartNumber);
                    bool rowOverloaded = false;
                    if (rowLimits != null)
                    {
                        double rowAmps = rowWatts / (rowLimits.Voltage <= 0 ? 120.0 : rowLimits.Voltage);
                        rowOverloaded = rowAmps > rowLimits.GetSlotLimit(slotNumber - 1) + 1e-9;
                    }
                    if (rowOverloaded)
                    {
                        var overloadFill = new XSolidBrush(XColor.FromArgb(255, 250, 210, 210));
                        gfx.DrawRectangle(overloadFill, MarginLeft, y, ContentWidth, LineHeight);
                    }
                    var rowBrush = rowOverloaded ? XBrushes.Red : XBrushes.Black;
                    var rowFont = rowOverloaded ? fontRowBold : fontRow;

                    // Slot #
                    gfx.DrawString(slotNumber.ToString(), rowFont, rowBrush,
                        new XPoint(colX[0] + colW[0] / 2, baseline), slotCenterAlign);

                    // Load (truncate if needed)
                    string loadName = circuit != null
                        ? (!string.IsNullOrWhiteSpace(circuit.UpdatedLoadName)
                            ? circuit.UpdatedLoadName
                            : circuit.CurrentLoadName)
                        : "";
                    double loadMaxWidth = colW[1] - ColumnPadding * 2;
                    if (gfx.MeasureString(loadName, rowFont).Width > loadMaxWidth && loadName.Length > 0)
                    {
                        while (loadName.Length > 1 && gfx.MeasureString(loadName + "\u2026", rowFont).Width > loadMaxWidth)
                            loadName = loadName[..^1];
                        loadName += "\u2026";
                    }
                    gfx.DrawString(loadName, rowFont, rowBrush,
                        new XPoint(colX[1] + ColumnPadding, baseline));

                    // Ckt
                    gfx.DrawString(cktNum, rowFont, rowBrush,
                        new XPoint(colX[2] + ColumnPadding, baseline));

                    // Dimming — the LOAD's protocol, not the module's type. The module header
                    // above already names the module; this column earns its place by describing
                    // the individual output (an MLV load on an ELV module reads "MLV").
                    // A circuit whose fixtures disagree yields a joined value ("0-10V; ELV") that
                    // can outrun the fixed column, so it truncates like the Load column does.
                    string dimming = module.SlotProtocol(slotNumber - 1) ?? "";
                    double dimmingMaxWidth = colW[3] - ColumnPadding * 2;
                    if (gfx.MeasureString(dimming, rowFont).Width > dimmingMaxWidth && dimming.Length > 0)
                    {
                        while (dimming.Length > 1
                               && gfx.MeasureString(dimming + "\u2026", rowFont).Width > dimmingMaxWidth)
                            dimming = dimming[..^1];
                        dimming += "\u2026";
                    }
                    gfx.DrawString(dimming, rowFont, rowBrush,
                        new XPoint(colX[3] + ColumnPadding, baseline));

                    // Watts
                    gfx.DrawString(FormatWatts(rowWatts), rowFont, rowBrush,
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

        // Release date left-aligned, mirroring the page number
        if (!string.IsNullOrWhiteSpace(settings.FooterDate))
            gfx.DrawString(settings.FooterDate, fontPageNum, XBrushes.Gray,
                new XPoint(MarginLeft, fTop + 10), XStringFormats.TopLeft);

        gfx.DrawString($"Page {pageNumber} of {pageCount}", fontPageNum, XBrushes.Gray,
            new XPoint(PageWidth - MarginRight, fTop + 10), XStringFormats.TopRight);
    }
}
