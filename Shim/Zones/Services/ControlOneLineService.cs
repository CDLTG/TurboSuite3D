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
    /// <b>wipe-and-redraw</b> from the <see cref="ControlOneLineDrawing"/> snapshot. Panels and shade boxes
    /// are <b>renderer-drawn</b> here (outline <c>DetailCurve</c>s + module tiles + footer <c>TextNote</c>s
    /// off <see cref="ControlOneLineGeometry.Panel"/>) — no node family — so the diagram draws with no
    /// authored artwork; only the optional wire-mark annotation is a family, and it degrades to "skip
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

            View lastOpened = null;
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
                        var dashed = ResolveLineStyle(new[] { "Wiring (CAT6)", "Dash", "Dashed", "Hidden", "<Hidden>" });
                        var solid = ResolveLineStyle(new[] { "Lighting Fixture", "<Solid>", "Solid", "Medium Lines", "Thin Lines" });
                        var textType = ResolveTextType();

                        long vid = viewRegistry != null && viewRegistry.TryGetValue(page.PageIndex, out long v) ? v : 0L;
                        var view = FindOrCreateViewByIdOrName(page.ViewName(systemName), vid, result.Warnings, out bool created);
                        if (view == null) { tx.RollBack(); results.Add(result); continue; }
                        result.Created = created;

                        _doc.Regenerate();   // a just-created view / duplicated text type must be a valid draw target
                        if (!created) WipeView(view);

                        foreach (var p in page.Panels) { DrawPanelNode(view, p, solid, textType, result.Warnings); result.Panels++; }
                        foreach (var s in page.Shades) { DrawShadeNode(view, s, solid, textType, result.Warnings); result.Shades++; }
                        foreach (var sym in page.Symbols) { DrawSymbol(view, sym, solid, textType, result.Warnings); result.Symbols++; }
                        result.Wires += DrawWires(view, page.Wires, dashed, solid);
                        result.Notes += DrawNotes(view, page.Notes, textType, result.Warnings);
                        result.Markers += DrawMarkers(view, page.Markers, marker);

                        result.ViewId = view.Id.ToRef().Value;
                        lastOpened = view;
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

            if (lastOpened != null)
            {
                try { _uidoc.ActiveView = lastOpened; } catch { /* non-fatal: leave the user where they are */ }
            }
            ReportWarnings(allWarnings);
            return results;
        }

        // ── Node drawing (renderer-drawn — no family) ────────────────────────────────────────────────
        // A panel = enclosure family (chosen by the node's EnclosureRole) + one module-tile family per filled
        // module slot + one LV-slot family per LV compartment, each positioned off the geometry. Every family
        // degrades to a renderer-drawn box + text when it isn't loaded, so the diagram draws with no artwork.
        private void DrawPanelNode(View view, ControlPanelNode node, GraphicsStyle solid, ElementId textType,
            List<string> warnings)
        {
            double h = G.Panel.Height(node.ModuleTiles.Count, node.LvSlots.Count);
            double cx = node.Center.X, cy = node.Center.Y;

            var enclosure = ResolveSymbol(node.EnclosureRole);
            if (enclosure != null)
                PlaceFamily(view, enclosure, node.Center,
                    (G.Panel.NameParam, node.Name), (G.Panel.FillParam, node.FillText), (G.Panel.PartNumberParam, node.PartNumber));
            else
                DrawPanelEnclosureFallback(view, node, h, solid, textType, warnings);

            var moduleFam = ResolveSymbol(Roles.ControlModuleDetail);
            for (int i = 0; i < node.ModuleTiles.Count; i++)
            {
                double ty = G.Panel.TileCenterY(cy, h, i);
                string part = node.ModuleTiles[i];
                if (moduleFam != null)
                {
                    if (!string.IsNullOrEmpty(part))   // empty slots are shown by the enclosure family's grid
                        PlaceFamily(view, moduleFam, new XY(cx, ty), (G.Module.PartNumberParam, part));
                }
                else DrawTileFallback(view, cx, ty, part ?? "empty", solid, textType, warnings);
            }

            var lvFam = ResolveSymbol(Roles.ControlLvSlotDetail);
            for (int j = 0; j < node.LvSlots.Count; j++)
            {
                double ty = G.Panel.TileCenterY(cy, h, node.ModuleTiles.Count + j);
                if (lvFam != null) PlaceFamily(view, lvFam, new XY(cx, ty), (G.LvSlot.LabelParam, node.LvSlots[j]));
                else DrawTileFallback(view, cx, ty, node.LvSlots[j], solid, textType, warnings);
            }
        }

        private void DrawPanelEnclosureFallback(View view, ControlPanelNode node, double h, GraphicsStyle solid,
            ElementId textType, List<string> warnings)
        {
            double cx = node.Center.X, cy = node.Center.Y, w = G.Panel.Width;
            DrawRect(view, cx, cy, w, h, solid);
            double footerTopY = cy - h / 2.0 + G.Panel.FooterHeight;
            DrawSegment(view, new XYZ(cx - w / 2.0, footerTopY, 0), new XYZ(cx + w / 2.0, footerTopY, 0), solid);
            double inset = G.Panel.TileInset;
            DrawText(view, new XY(cx - w / 2.0 + inset, footerTopY - 1.0 / 12.0), node.Name, ControlTextAlign.Left, textType, warnings);
            DrawText(view, new XY(cx - w / 2.0 + inset, footerTopY - 7.0 / 12.0), node.FillText, ControlTextAlign.Left, textType, warnings);
            DrawText(view, new XY(cx + w / 2.0 - inset, footerTopY - 1.0 / 12.0), node.PartNumber, ControlTextAlign.Right, textType, warnings);
        }

        private void DrawTileFallback(View view, double cx, double ty, string label, GraphicsStyle solid,
            ElementId textType, List<string> warnings)
        {
            DrawRect(view, cx, ty, G.Panel.TileWidth, G.Panel.TileHeight, solid);
            DrawText(view, new XY(cx, ty), label, ControlTextAlign.Center, textType, warnings, centerV: true);
        }

        private void DrawShadeNode(View view, ControlShadeNode node, GraphicsStyle solid, ElementId textType,
            List<string> warnings)
        {
            var fam = ResolveSymbol(Roles.ControlSmartPanelDetail);
            if (fam != null)
            {
                PlaceFamily(view, fam, node.Center,
                    (G.ShadePanel.NameParam, node.Name), (G.ShadePanel.FillParam, node.FillText), (G.ShadePanel.PartNumberParam, node.PartNumber));
                return;
            }
            double cx = node.Center.X, cy = node.Center.Y, w = G.ShadePanel.Width, h = G.ShadePanel.Height;
            DrawRect(view, cx, cy, w, h, solid);
            DrawText(view, new XY(cx, cy + h / 2.0 - 2.0 / 12.0), node.PartNumber, ControlTextAlign.Center, textType, warnings);
            double inset = G.Panel.TileInset;
            DrawText(view, new XY(cx - w / 2.0 + inset, cy - h / 2.0 + 5.0 / 12.0), node.Name, ControlTextAlign.Left, textType, warnings);
            DrawText(view, new XY(cx + w / 2.0 - inset, cy - h / 2.0 + 5.0 / 12.0), node.FillText, ControlTextAlign.Right, textType, warnings);
        }

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

        private void DrawSymbol(View view, ControlSymbolInstance sym, GraphicsStyle solid, ElementId textType,
            List<string> warnings)
        {
            if (sym.Kind == ControlSymbolKind.HomeNetwork)
            {
                double cx = sym.Position.X, cy = sym.Position.Y;
                double w = G.Layout.HomeNetworkWidth, h = G.Layout.HomeNetworkHeight;
                DrawRect(view, cx, cy, w, h, solid);
                DrawText(view, new XY(cx, cy + 2.0 / 12.0), "HOME NETWORK", ControlTextAlign.Center, textType, warnings);
                DrawText(view, new XY(cx, cy - 3.0 / 12.0), "LAN SWITCH", ControlTextAlign.Center, textType, warnings);
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
            int drawn = 0;
            foreach (var n in notes)
            {
                var opts = new TextNoteOptions(textType) { HorizontalAlignment = Align(n.Align), Rotation = 0.0 };
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

        // ── Primitive draw helpers ───────────────────────────────────────────────────────────────────
        private void DrawRect(View view, double cx, double cy, double w, double h, GraphicsStyle style)
        {
            double x0 = cx - w / 2.0, x1 = cx + w / 2.0, y0 = cy - h / 2.0, y1 = cy + h / 2.0;
            var bl = new XYZ(x0, y0, 0); var br = new XYZ(x1, y0, 0);
            var tr = new XYZ(x1, y1, 0); var tl = new XYZ(x0, y1, 0);
            DrawSegment(view, bl, br, style);
            DrawSegment(view, br, tr, style);
            DrawSegment(view, tr, tl, style);
            DrawSegment(view, tl, bl, style);
        }

        private void DrawSegment(View view, XYZ a, XYZ b, GraphicsStyle style)
        {
            if (a.IsAlmostEqualTo(b)) return;
            var dc = _doc.Create.NewDetailCurve(view, Line.CreateBound(a, b));
            if (style != null) { try { dc.LineStyle = style; } catch { /* leave default */ } }
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
