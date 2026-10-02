#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Abstractions;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;
using TurboSuite.Zones.OneLine;
using G = TurboSuite.Zones.OneLine.ControlOneLineGeometry;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Shim-side <see cref="IControlOneLineService"/> — the control one-line generator. Each 42×30 page OWNS
    /// a Drafting View (deterministic name + persisted id, keyed by page index), so a draw is a pure
    /// <b>wipe-and-redraw</b> from the <see cref="ControlOneLineDrawing"/> snapshot. Panels and shade nodes are
    /// <b>family-composed</b> — an enclosure family (by <c>EnclosureRole</c>) plus a module/LV tile family per
    /// slot, positioned off <see cref="ControlOneLineGeometry.Panel"/>; a missing node family <b>warns once and
    /// skips</b> (there is no box + text stand-in). Wires, notes, and the HOME NETWORK glyph are drawn natively
    /// (<c>DetailCurve</c>s / <c>TextNote</c>s); the wire-mark annotation is a family that degrades to "skip
    /// markers" when absent. Reuses the TurboDMX one-line service's view-ownership, wipe, wire/note/marker,
    /// and resolution helpers. One transaction per page on the API thread via the work queue.
    /// </summary>
    public sealed class ControlOneLineService : IControlOneLineService
    {
        private readonly UIDocument _uidoc;
        private readonly Document _doc;

        public ControlOneLineService(UIDocument uidoc)
        {
            _uidoc = uidoc;
            _doc = uidoc.Document;
        }

        public IReadOnlyList<ControlOneLineResult> Draw(IReadOnlyList<ControlOneLineDrawing> pages,
            string systemName, IReadOnlyDictionary<int, long> viewRegistry)
        {
            var results = new List<ControlOneLineResult>();
            var allWarnings = new List<string>();
            if (pages == null || pages.Count == 0)
            {
                allWarnings.Add("No solved one-line to draw.");
                ReportWarnings(allWarnings);
                return results;
            }
            if (string.IsNullOrWhiteSpace(systemName)) systemName = "TurboControl";

            View firstOpened = null;
            foreach (var page in pages)
            {
                var result = new ControlOneLineResult { PageIndex = page.PageIndex };
                using (var tx = new Transaction(_doc, $"TurboZones — One-line sheet {page.PageIndex}"))
                {
                    tx.Start();
                    try
                    {
                        var marker = ResolveSymbol(Roles.ControlWireMarkAnnotation);
                        if (marker == null)
                            result.Warnings.Add($"No {Roles.Label(Roles.ControlWireMarkAnnotation)} loaded — markers skipped.");
                        // Lutron line-style convention: dashed = RF (the "Wiring (CAT6)" dashed style), solid =
                        // wired = the Revit default line style — which is named "<Medium Lines>" (angle-bracketed)
                        // in OST_Lines.SubCategories; the unbracketed "Medium Lines" is a Detail Items style, not
                        // a line style. Every segment is wired/solid today. (Confirmed by TurboSpike.)
                        var dashed = ResolveLineStyle(new[] { "Wiring (CAT6)", "Dash", "Dashed", "<Hidden>", "Hidden" });
                        var solid = ResolveLineStyle(new[] { "<Medium Lines>", "<Thin Lines>", "<Wide Lines>", "Lighting Fixture", "<Solid>", "Solid" });
                        var textType = ResolveTextType();

                        // Node families are the only path — a missing one warns once (deduped) and skips its
                        // pieces; there is no renderer-drawn box fallback. Enclosure role varies per node, so it
                        // is resolved inside DrawPanelNode against the same warned-role set.
                        var warnedRoles = new HashSet<string>();
                        var moduleFam = ResolveSymbol(Roles.ControlModuleDetail);
                        var lvFam = ResolveSymbol(Roles.ControlLvSlotDetail);
                        var shadeFam = ResolveSymbol(Roles.ControlSmartPanelDetail);

                        long vid = viewRegistry != null && viewRegistry.TryGetValue(page.PageIndex, out long v) ? v : 0L;
                        var view = FindOrCreateViewByIdOrName(page.ViewName(systemName), vid, result.Warnings, out bool created);
                        if (view == null) { tx.RollBack(); results.Add(result); continue; }
                        result.Created = created;

                        _doc.Regenerate();   // a just-created view / duplicated text type must be a valid draw target
                        if (!created) WipeView(view);

                        foreach (var p in page.Panels) { DrawPanelNode(view, p, moduleFam, lvFam, result.Warnings, warnedRoles); result.Panels++; }
                        foreach (var s in page.Shades) { DrawShadeNode(view, s, shadeFam, result.Warnings, warnedRoles); result.Shades++; }
                        result.Wires += DrawWires(view, page.Wires, dashed, solid);
                        result.Notes += DrawNotes(view, page.Notes, textType, result.Warnings);
                        result.Markers += DrawMarkers(view, page.Markers, marker);

                        result.ViewId = view.Id.ToRef().Value;
                        if (firstOpened == null) firstOpened = view;   // land on sheet 1, the start of the diagram
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"One-line sheet {page.PageIndex} draw failed — {ex.Message}");
                        if (tx.HasStarted()) tx.RollBack();
                        result.ViewId = 0L;
                    }
                }
                results.Add(result);
                allWarnings.AddRange(result.Warnings);
            }

            if (firstOpened != null)
            {
                try { _uidoc.ActiveView = firstOpened; } catch { /* non-fatal: leave the user where they are */ }
            }
            // A rebuild with FEWER pages than a prior run leaves higher-numbered owned sheets orphaned — delete
            // them (done AFTER the active-view switch above, so a stale sheet is never the active view being
            // deleted). By-name so it catches orphans a fresh session's empty registry would miss.
            PruneStaleSheets(pages.Count, systemName, allWarnings);
            ReportWarnings(allWarnings);
            return results;
        }

        public ControlWireLegendResult DrawWireLegend(ControlWireLegendDrawing drawing, string systemName,
            long existingViewId)
        {
            var result = new ControlWireLegendResult();
            if (drawing == null) { result.Warnings.Add("No solved wire legend to draw."); ReportWarnings(result.Warnings); return result; }
            if (string.IsNullOrWhiteSpace(systemName)) systemName = "TurboControl";

            using (var tx = new Transaction(_doc, "TurboZones — Wire legend"))
            {
                tx.Start();
                try
                {
                    var marker = ResolveSymbol(Roles.ControlWireMarkAnnotation);
                    if (marker == null)
                        result.Warnings.Add($"No {Roles.Label(Roles.ControlWireMarkAnnotation)} loaded — numbers skipped.");
                    var textType = ResolveTextType();
                    // Same dashed (RF) / solid (wired) styles the one-line uses, so the RF/WIRED sample keys match
                    // the sheet; the border rides the solid style. Solid = the bracketed "<Medium Lines>".
                    var dashed = ResolveLineStyle(new[] { "Wiring (CAT6)", "Dash", "Dashed", "<Hidden>", "Hidden" });
                    var solid = ResolveLineStyle(new[] { "<Medium Lines>", "<Thin Lines>", "<Wide Lines>", "Lighting Fixture", "<Solid>", "Solid" });

                    var view = FindOrCreateViewByIdOrName(drawing.ViewName(systemName), existingViewId, result.Warnings, out bool created);
                    if (view == null) { tx.RollBack(); ReportWarnings(result.Warnings); return result; }
                    result.Created = created;

                    _doc.Regenerate();   // a just-created view / duplicated text type must be a valid draw target
                    if (!created) WipeView(view);

                    DrawNotes(view, drawing.Notes, textType, result.Warnings);
                    result.Rows += DrawMarkers(view, drawing.Markers, marker);
                    DrawWires(view, drawing.SampleLines, dashed, solid);   // the RF/WIRED line-style key samples

                    // Center the title over the rendered row block, then enclose everything in a border.
                    _doc.Regenerate();
                    DrawTitleCentered(view, drawing.Title, textType, result.Warnings);
                    _doc.Regenerate();
                    DrawLegendBorder(view, solid);

                    result.ViewId = view.Id.ToRef().Value;
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Wire-legend draw failed — {ex.Message}");
                    if (tx.HasStarted()) tx.RollBack();
                    result.ViewId = 0L;
                }
            }

            // Deliberately do NOT switch the active view to the legend: this is always chained AFTER the pages
            // in the same Draw, so the user should be left on the diagram (sheet 1), not the legend.
            ReportWarnings(result.Warnings);
            return result;
        }

        // Draw the legend title centered over the rendered row block (measured now on the page). The title
        // carries the firm's named style; fall back to a size match, then the default type.
        private void DrawTitleCentered(View view, ControlNote title, ElementId defaultType, List<string> warnings)
        {
            if (title == null) return;

            double centerX = TryUnionViewBox(view, out var min, out var max)
                ? (min.X + max.X) / 2.0
                : title.Position.X;

            var type = ElementId.InvalidElementId;
            if (!string.IsNullOrEmpty(title.TextTypeName)) type = ResolveTextTypeByName(title.TextTypeName, warnings);
            if (type == ElementId.InvalidElementId && title.TextHeightFt is double h) type = ResolveTextTypeBySize(h);
            if (type == ElementId.InvalidElementId) type = defaultType;
            if (type == ElementId.InvalidElementId) { warnings.Add("No text type — title skipped."); return; }

            var opts = new TextNoteOptions(type) { HorizontalAlignment = HorizontalTextAlignment.Center, Rotation = 0.0 };
            TextNote.Create(_doc, view.Id, new XYZ(centerX, title.Position.Y, 0.0), title.Text, opts);
        }

        // Rectangle hugging the combined legend extents, offset out by BorderOffset (top edge trimmed to absorb
        // the TextNote bbox headroom). Verbatim from the DMX legend border.
        private void DrawLegendBorder(View view, GraphicsStyle solid)
        {
            if (!TryUnionViewBox(view, out var min, out var max)) return;
            double o = G.Legend.BorderOffset;
            double topY = max.Y + o - G.Legend.BorderTopTrim;
            var bl = new XYZ(min.X - o, min.Y - o, 0.0);
            var br = new XYZ(max.X + o, min.Y - o, 0.0);
            var tr = new XYZ(max.X + o, topY, 0.0);
            var tl = new XYZ(min.X - o, topY, 0.0);
            DrawSegment(view, bl, br, solid);
            DrawSegment(view, br, tr, solid);
            DrawSegment(view, tr, tl, solid);
            DrawSegment(view, tl, bl, solid);
        }

        private void DrawSegment(View view, XYZ a, XYZ b, GraphicsStyle style)
        {
            var dc = _doc.Create.NewDetailCurve(view, Line.CreateBound(a, b));
            if (style != null) { try { dc.LineStyle = style; } catch { /* style not applicable — leave default */ } }
        }

        // Union of every non-type element's view bounding box; false if the view has nothing to bound.
        private bool TryUnionViewBox(View view, out XYZ min, out XYZ max)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            bool any = false;
            foreach (var id in new FilteredElementCollector(_doc, view.Id).WhereElementIsNotElementType().ToElementIds())
            {
                var bb = _doc.GetElement(id)?.get_BoundingBox(view);
                if (bb == null) continue;
                any = true;
                minX = Math.Min(minX, bb.Min.X); minY = Math.Min(minY, bb.Min.Y);
                maxX = Math.Max(maxX, bb.Max.X); maxY = Math.Max(maxY, bb.Max.Y);
            }
            min = new XYZ(minX, minY, 0.0);
            max = new XYZ(maxX, maxY, 0.0);
            return any;
        }

        // First existing TextNoteType at the given paper size (feet), or Invalid if the project has none.
        private ElementId ResolveTextTypeBySize(double sizeFt) =>
            new FilteredElementCollector(_doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
                .FirstOrDefault(t =>
                {
                    var p = t.get_Parameter(BuiltInParameter.TEXT_SIZE);
                    return p != null && Math.Abs(p.AsDouble() - sizeFt) < 1e-4;
                })?.Id ?? ElementId.InvalidElementId;

        // ── Node drawing (family-composed — no renderer-drawn fallback) ──────────────────────────────
        // A panel = enclosure family (chosen by the node's EnclosureRole) + one module-tile family per filled
        // module slot + one LV-slot family per LV compartment, each placed off the geometry. Families are the
        // ONLY path: a missing family warns once (deduped via warnedRoles) and skips its pieces — never a
        // box + text stand-in that could be mistaken for finished artwork or mask a mis-roled/unloaded family.
        private void DrawPanelNode(View view, ControlPanelNode node, FamilySymbol moduleFam, FamilySymbol lvFam,
            List<string> warnings, HashSet<string> warnedRoles)
        {
            var moduleTiles = node.ModuleTiles;
            var lvSlots = node.LvSlots;
            int total = moduleTiles.Count + lvSlots.Count;
            double h = G.Panel.Height(moduleTiles.Count, lvSlots.Count);
            double cx = node.Center.X, cy = node.Center.Y;
            double originY = cy - h / 2.0;   // enclosure family origin (bottom-center) after PlaceFamilyGrowUp

            var enclosure = ResolveSymbol(node.EnclosureRole);
            if (enclosure != null)
                // Families are authored bottom-origin (asymmetric — the artwork grows UP from the insertion
                // point), so place the enclosure at the band BOTTOM (center − h/2); its art then fills the
                // [center ± h/2] band. See PlaceFamilyGrowUp.
                PlaceFamilyGrowUp(view, enclosure, cx, cy, h,
                    (G.Panel.NameParam, node.Name), (G.Panel.FillParam, node.FillText), (G.Panel.PartNumberParam, node.PartNumber));
            else WarnMissing(node.EnclosureRole, warnings, warnedRoles);

            // Tile anchor Y (absolute) for top-down slot index `slot`. Tile families anchor by ORIGIN at the
            // rung spot (art positioned around the origin), so they are placed AT the point — no band. The
            // authored bottom-up rung offsets are picked by tile count: a power panel (9) uses RungOffsets, an
            // LV21 (2) uses Lv21RungOffsets; anything else falls back to the derived grid. RungOffsets is
            // bottom-up so a top-down slot maps to [total - 1 - slot].
            double[] rungs =
                total == G.Panel.RungOffsets.Length ? G.Panel.RungOffsets :
                total == G.Panel.Lv21RungOffsets.Length ? G.Panel.Lv21RungOffsets : null;
            double AnchorY(int slot) =>
                rungs != null
                    ? originY + rungs[total - 1 - slot]
                    : G.Panel.TileCenterY(cy, h, slot);

            // Module tiles fill the top slots (0..count-1); LV slots the bottom (count..) — the bottom rung
            // holds the LV compartment on a PD8, a module on a PD9.
            for (int i = 0; i < moduleTiles.Count; i++)
            {
                string part = moduleTiles[i];
                if (string.IsNullOrEmpty(part)) continue;   // empty slots are shown by the enclosure family's grid
                if (moduleFam == null) { WarnMissing(Roles.ControlModuleDetail, warnings, warnedRoles); continue; }
                PlaceFamily(view, moduleFam, new XY(cx, AnchorY(i)), (G.Module.PartNumberParam, part));
            }

            for (int j = 0; j < lvSlots.Count; j++)
            {
                if (lvFam == null) { WarnMissing(Roles.ControlLvSlotDetail, warnings, warnedRoles); continue; }
                PlaceFamily(view, lvFam, new XY(cx, AnchorY(moduleTiles.Count + j)), (G.LvSlot.LabelParam, lvSlots[j]));
            }
        }

        private void DrawShadeNode(View view, ControlShadeNode node, FamilySymbol shadeFam,
            List<string> warnings, HashSet<string> warnedRoles)
        {
            if (shadeFam == null) { WarnMissing(Roles.ControlSmartPanelDetail, warnings, warnedRoles); return; }
            PlaceFamilyGrowUp(view, shadeFam, node.Center.X, node.Center.Y, G.ShadePanel.Height,
                (G.ShadePanel.NameParam, node.Name), (G.ShadePanel.FillParam, node.FillText), (G.ShadePanel.PartNumberParam, node.PartNumber));
        }

        // Warn once per missing node-family role (deduped), so a job with an unloaded family reports it rather
        // than drawing a look-alike box.
        private static void WarnMissing(string role, List<string> warnings, HashSet<string> warnedRoles)
        {
            if (warnedRoles.Add(role))
                warnings.Add($"No {Roles.Label(role)} loaded — node skipped.");
        }

        // Place a bottom-origin family (artwork expands UP from its insertion point) so it fills a band whose
        // CENTER is (centerX, bandCenterY) and whose height is bandHeight. The planner works in band centers
        // (node.Center / TileCenterY); this derives the bottom-center insertion point (center − height/2),
        // keeping all layout math center-based while honoring the grow-up authoring convention.
        private void PlaceFamilyGrowUp(View view, FamilySymbol sym, double centerX, double bandCenterY,
            double bandHeight, params (string param, string value)[] ps)
            => PlaceFamily(view, sym, new XY(centerX, bandCenterY - bandHeight / 2.0), ps);

        // Place a family instance and write its label params (skipping nulls / read-only / missing params).
        private void PlaceFamily(View view, FamilySymbol sym, XY at, params (string param, string value)[] ps)
        {
            if (!sym.IsActive) { sym.Activate(); _doc.Regenerate(); }
            var inst = _doc.Create.NewFamilyInstance(Pt(at), sym, view);
            foreach (var pv in ps)
            {
                if (pv.value == null) continue;
                var par = inst.LookupParameter(pv.param);
                if (par != null && !par.IsReadOnly) par.Set(pv.value);
            }
        }

        // ── Reused-from-DMX passes (adapted to the Control types) ────────────────────────────────────
        private int DrawWires(View view, IReadOnlyList<ControlWireSegment> wires, GraphicsStyle dashed, GraphicsStyle solid)
        {
            int drawn = 0;
            foreach (var wseg in wires)
            {
                if (wseg.Start.X == wseg.End.X && wseg.Start.Y == wseg.End.Y) continue; // zero-length guard
                var dc = _doc.Create.NewDetailCurve(view, Line.CreateBound(Pt(wseg.Start), Pt(wseg.End)));
                var style = wseg.Dashed ? dashed : solid;
                if (style != null) { try { dc.LineStyle = style; } catch { /* style not applicable */ } }
                drawn++;
            }
            return drawn;
        }

        private int DrawNotes(View view, IReadOnlyList<ControlNote> notes, ElementId textType, List<string> warnings)
        {
            if (textType == ElementId.InvalidElementId) { warnings.Add("No text type — notes skipped."); return 0; }
            var namedCache = new Dictionary<string, ElementId>(StringComparer.Ordinal);
            int drawn = 0;
            foreach (var n in notes)
            {
                var typeId = textType;
                if (!string.IsNullOrEmpty(n.TextTypeName))
                {
                    if (!namedCache.TryGetValue(n.TextTypeName, out typeId))
                    {
                        typeId = ResolveTextTypeByName(n.TextTypeName, warnings);
                        if (typeId == ElementId.InvalidElementId) typeId = textType;   // fall back once, cached
                        namedCache[n.TextTypeName] = typeId;
                    }
                }
                var opts = new TextNoteOptions(typeId) { HorizontalAlignment = Align(n.Align), Rotation = 0.0 };
                TextNote.Create(_doc, view.Id, Pt(n.Position), n.Text, opts);
                drawn++;
            }
            return drawn;
        }

        private int DrawMarkers(View view, IReadOnlyList<ControlMarker> markers, FamilySymbol marker)
        {
            if (marker == null) return 0;
            if (!marker.IsActive) { marker.Activate(); _doc.Regenerate(); }
            int drawn = 0;
            foreach (var m in markers)
            {
                var inst = _doc.Create.NewFamilyInstance(Pt(m.Position), marker, view);
                var p = inst.LookupParameter(G.Marker.NumberParam);
                if (p != null && !p.IsReadOnly) p.Set(m.Mark);
                drawn++;
            }
            return drawn;
        }


        // A TextNote anchors at its top edge (no vertical-alignment API). centerV raises the insertion point
        // by half the model-space cap height so the glyph sits centered on a tile's midline.
        private void DrawText(View view, XY at, string text, ControlTextAlign align, ElementId textType,
            List<string> warnings, bool centerV = false)
        {
            if (textType == ElementId.InvalidElementId) return;   // already warned once by DrawNotes
            if (string.IsNullOrEmpty(text)) return;
            double y = at.Y + (centerV ? G.NoteTextHeightFt * G.ViewScale / 2.0 : 0.0);
            var opts = new TextNoteOptions(textType) { HorizontalAlignment = Align(align), Rotation = 0.0 };
            TextNote.Create(_doc, view.Id, new XYZ(at.X, y, 0.0), text, opts);
        }

        // ── View ownership (verbatim pattern from DmxOneLineService) ─────────────────────────────────
        private View FindOrCreateViewByIdOrName(string name, long existingViewId, List<string> warnings, out bool created)
        {
            created = false;

            if (existingViewId != 0L
                && _doc.GetElement(new ElementRef(existingViewId).ToElementId()) is ViewDrafting byId && !byId.IsTemplate)
                return byId;

            var byName = new FilteredElementCollector(_doc).OfClass(typeof(ViewDrafting)).Cast<ViewDrafting>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, name, StringComparison.Ordinal));
            if (byName != null) return byName;

            var vft = new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.Drafting);
            if (vft == null) { warnings.Add("No Drafting view type in the project — cannot create the view."); return null; }

            var view = ViewDrafting.Create(_doc, vft.Id);
            TrySetName(view, name);
            try { view.Scale = G.ViewScale; } catch { /* some templates lock scale */ }
            created = true;
            return view;
        }

        // Delete owned one-line sheets numbered above the kept count (a job that shrank from N pages to fewer).
        // Matched by the deterministic view name "{systemName} One-Line - Sheet {index}" so it works whether or
        // not the in-session registry still holds the orphan's id (a fresh session starts with an empty one).
        private void PruneStaleSheets(int keptPageCount, string systemName, List<string> warnings)
        {
            string prefix = $"{systemName} One-Line - Sheet ";
            var stale = new FilteredElementCollector(_doc).OfClass(typeof(ViewDrafting)).Cast<ViewDrafting>()
                .Where(v => !v.IsTemplate && v.Name.StartsWith(prefix, StringComparison.Ordinal)
                            && int.TryParse(v.Name.Substring(prefix.Length), out int idx) && idx > keptPageCount)
                .Select(v => v.Id).ToList();
            if (stale.Count == 0) return;
            using (var tx = new Transaction(_doc, "TurboZones — remove stale one-line sheets"))
            {
                tx.Start();
                try { _doc.Delete(stale); tx.Commit(); }
                catch (Exception ex)
                {
                    if (tx.HasStarted()) tx.RollBack();
                    warnings.Add($"Could not remove {stale.Count} stale one-line sheet(s) — {ex.Message}");
                }
            }
        }

        private void TrySetName(View view, string name)
        {
            try { view.Name = name; }
            catch { try { view.Name = name + " " + Guid.NewGuid().ToString("N").Substring(0, 4); } catch { /* keep default */ } }
        }

        // Wipe only the element kinds we draw (DetailCurves, TextNotes, FamilyInstances). See the DMX service's
        // note: the raw view-scoped set also holds a categoryless internal Element whose deletion cascades to
        // the view itself; the type filter skips it, and OwnedByView is empty for drafting content.
        private void WipeView(View view)
        {
            var ids = new FilteredElementCollector(_doc, view.Id).WhereElementIsNotElementType()
                .Where(e => e is CurveElement || e is TextNote || e is FamilyInstance)
                .Select(e => e.Id).ToList();
            if (ids.Count == 0) return;
            try { _doc.Delete(ids); } catch { /* best-effort */ }
        }

        // ── Resolution helpers (from DmxOneLineService) ──────────────────────────────────────────────
        private FamilySymbol ResolveSymbol(string role) =>
            new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(s => ParameterHelper.GetRole(s) == role);

        private GraphicsStyle ResolveLineStyle(string[] names)
        {
            var lines = _doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            if (lines == null) return null;
            foreach (var name in names)
                foreach (Category sub in lines.SubCategories)
                    if (string.Equals(sub.Name, name, StringComparison.OrdinalIgnoreCase))
                        return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
            return null;
        }

        private ElementId ResolveTextType()
        {
            double target = G.NoteTextHeightFt;
            var types = new FilteredElementCollector(_doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
            var match = types.FirstOrDefault(t =>
            {
                var p = t.get_Parameter(BuiltInParameter.TEXT_SIZE);
                return p != null && Math.Abs(p.AsDouble() - target) < 1e-4;
            });
            if (match != null) return match.Id;

            var baseType = types.FirstOrDefault();
            if (baseType == null) return ElementId.InvalidElementId;
            try
            {
                if (baseType.Duplicate("TurboControl 1-16in") is TextNoteType dup)
                {
                    dup.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(target);
                    return dup.Id;
                }
            }
            catch { /* name clash or locked — fall back to the base type */ }
            return baseType.Id;
        }

        // Resolve a TextNoteType by exact name (the firm's named styles, e.g. AL_Annotation_4.5"). Returns
        // InvalidElementId (warned once) when absent, so the caller can fall back to the generic type.
        private ElementId ResolveTextTypeByName(string name, List<string> warnings)
        {
            var match = new FilteredElementCollector(_doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
                .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
            if (match != null) return match.Id;
            warnings.Add($"Text style \"{name}\" not found — using the generic style.");
            return ElementId.InvalidElementId;
        }

        private static HorizontalTextAlignment Align(ControlTextAlign a) => a switch
        {
            ControlTextAlign.Right => HorizontalTextAlignment.Right,
            ControlTextAlign.Center => HorizontalTextAlignment.Center,
            _ => HorizontalTextAlignment.Left,
        };

        private static XYZ Pt(XY p) => new XYZ(p.X, p.Y, 0.0);

        private static void ReportWarnings(List<string> warnings)
        {
            if (warnings == null || warnings.Count == 0) return;
            TaskDialog.Show("TurboZones", string.Join("\n", warnings.Take(12)));
        }
    }
}
