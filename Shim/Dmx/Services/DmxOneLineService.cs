#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using TurboSuite.Abstractions;
using TurboSuite.Dmx.OneLine;
using TurboSuite.Dmx.Services;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Dmx.Services
{
    /// <summary>
    /// Shim-side <see cref="IDmxOneLineService"/> — the per-loop one-line generator (BuildPlan Phase 4). The
    /// program OWNS one Drafting View per loop (deterministic name + the persisted view id), so a draw is a
    /// pure <b>wipe-and-redraw</b> from the <see cref="DmxOneLineDrawing"/> snapshot: find/create the owned
    /// view, delete everything in it, then replay the symbols (Detail Items with their label params), the
    /// wires (<c>DetailCurve</c>s, solid power / dashed control), the native notes (<c>TextNote</c>s at
    /// 1/16"), and the wire-type markers (Generic Annotations). The designer drops the finished view onto a
    /// print sheet by hand; the static WIRE LEGEND is author-once and never drawn here (§8a).
    ///
    /// All geometry is model feet straight off <see cref="DmxOneLineGeometry"/>; the whole draw is one
    /// transaction (no pick — the diagram is generated, not click-placed), run on the API thread via the
    /// work queue. Missing families / line styles degrade to warnings, never a crash.
    /// </summary>
    public sealed class DmxOneLineService : IDmxOneLineService
    {
        private readonly UIDocument _uidoc;
        private readonly Document _doc;

        public DmxOneLineService(UIDocument uidoc)
        {
            _uidoc = uidoc;
            _doc = uidoc.Document;
        }

        public DmxOneLineResult Draw(IReadOnlyList<DmxOneLineDrawing> drawings, string systemName,
                                     int onlyInterfaceNumber, IReadOnlyDictionary<int, long> viewRegistry)
        {
            var result = new DmxOneLineResult { InterfaceNumber = onlyInterfaceNumber };
            var drawing = drawings?.FirstOrDefault(d => d.InterfaceNumber == onlyInterfaceNumber);
            if (drawing == null)
            {
                result.Warnings.Add($"No solved one-line for interface #{onlyInterfaceNumber}.");
                ReportWarnings(result);
                return result;
            }
            if (string.IsNullOrWhiteSpace(systemName)) systemName = "DMX";

            View opened = null;
            using (var tx = new Transaction(_doc, $"TurboDMX — One-line interface #{onlyInterfaceNumber}"))
            {
                tx.Start();
                try
                {
                    var symbols = ResolveSymbols(result);
                    var marker = ResolveSymbol(DmxOneLineGeometry.Marker.Family, DmxOneLineGeometry.Marker.Type);
                    if (marker == null) result.Warnings.Add($"Wire-mark family \"{DmxOneLineGeometry.Marker.Family}\" not loaded — markers skipped.");
                    var dashed = ResolveLineStyle(new[] { "Dash", "Dashed", "Hidden", "<Hidden>" });
                    var solid = ResolveLineStyle(new[] { "<Solid>", "Solid", "Medium Lines", "Thin Lines" });
                    var textType = ResolveTextType();

                    var view = FindOrCreateView(drawing, systemName, viewRegistry, result);
                    if (view == null) { tx.RollBack(); ReportWarnings(result); return result; }

                    // Regenerate so a just-created view (and any duplicated text type) are valid query/draw
                    // targets — placing into / collecting from an un-regenerated new element throws
                    // InvalidObjectException ("the referenced object is not valid").
                    _doc.Regenerate();

                    if (!result.Created) WipeView(view);   // a brand-new view has nothing to wipe
                    DrawSymbols(view, drawing, symbols, result);
                    DrawWires(view, drawing, dashed, solid, result);
                    result.Notes += DrawNotes(view, drawing.Notes, textType, result.Warnings);
                    result.Markers += DrawMarkers(view, drawing.Markers, marker, result.Warnings);

                    result.ViewId = view.Id.ToRef().Value;
                    opened = view;
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"One-line draw failed — {ex.Message}");
                    if (tx.HasStarted()) tx.RollBack();
                    result.ViewId = 0L;
                }
            }

            if (opened != null)
            {
                try { _uidoc.ActiveView = opened; } catch { /* non-fatal: leave the user where they are */ }
            }
            ReportWarnings(result);
            return result;
        }

        // ── Per-job wire legend (BuildPlan Phase 6): one owned view, same wipe-and-redraw as the one-line ──
        public DmxWireLegendResult DrawWireLegend(DmxWireLegendDrawing drawing, string systemName, long existingViewId)
        {
            var result = new DmxWireLegendResult();
            if (drawing == null) { result.Warnings.Add("No solved wire legend to draw."); ReportWarnings(result); return result; }
            if (string.IsNullOrWhiteSpace(systemName)) systemName = "DMX";

            View opened = null;
            using (var tx = new Transaction(_doc, "TurboDMX — Wire legend"))
            {
                tx.Start();
                try
                {
                    var marker = ResolveSymbol(DmxOneLineGeometry.Marker.Family, DmxOneLineGeometry.Marker.Type);
                    if (marker == null) result.Warnings.Add($"Wire-mark family \"{DmxOneLineGeometry.Marker.Family}\" not loaded — numbers skipped.");
                    var textType = ResolveTextType();

                    string name = drawing.ViewName(systemName);
                    var view = FindOrCreateViewByIdOrName(name, existingViewId, result.Warnings, out bool created);
                    if (view == null) { tx.RollBack(); ReportWarnings(result); return result; }
                    result.Created = created;

                    _doc.Regenerate();   // a just-created view / duplicated text type must be a valid draw target

                    if (!created) WipeView(view);
                    DrawNotes(view, drawing.Notes, textType, result.Warnings);
                    result.Rows += DrawMarkers(view, drawing.Markers, marker, result.Warnings);

                    result.ViewId = view.Id.ToRef().Value;
                    opened = view;
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Wire-legend draw failed — {ex.Message}");
                    if (tx.HasStarted()) tx.RollBack();
                    result.ViewId = 0L;
                }
            }

            if (opened != null)
            {
                try { _uidoc.ActiveView = opened; } catch { /* non-fatal */ }
            }
            ReportWarnings(result);
            return result;
        }

        // ── View ownership: registry id → by name → create ───────────────────────────────────────────
        private View FindOrCreateView(DmxOneLineDrawing drawing, string systemName,
                                      IReadOnlyDictionary<int, long> viewRegistry, DmxOneLineResult result)
        {
            string name = drawing.ViewName(systemName);
            long vid = viewRegistry != null && viewRegistry.TryGetValue(drawing.InterfaceNumber, out long v) ? v : 0L;
            var view = FindOrCreateViewByIdOrName(name, vid, result.Warnings, out bool created);
            if (created) result.Created = true;
            return view;
        }

        // Shared owned-view resolver: persisted id → by name → create. Sets <paramref name="created"/> true
        // only when a brand-new view was made (so the caller skips the wipe).
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
            try { view.Scale = DmxOneLineGeometry.ViewScale; } catch { /* some templates lock scale */ }
            created = true;
            return view;
        }

        private void TrySetName(View view, string name)
        {
            try { view.Name = name; }
            catch { try { view.Name = name + " " + Guid.NewGuid().ToString("N").Substring(0, 4); } catch { /* keep default */ } }
        }

        // Wipe everything the view owns (it's program-owned, so a full clear is safe — Phase 4 wipe-and-redraw).
        private void WipeView(View view)
        {
            var ids = new FilteredElementCollector(_doc, view.Id).WhereElementIsNotElementType().ToElementIds();
            if (ids.Count == 0) return;
            try { _doc.Delete(ids); } catch { /* best-effort: a pinned/undeletable element shouldn't abort the redraw */ }
        }

        // ── Drawing passes ───────────────────────────────────────────────────────────────────────────
        private void DrawSymbols(View view, DmxOneLineDrawing drawing,
                                 IReadOnlyDictionary<DmxSymbolKind, FamilySymbol> symbols, DmxOneLineResult result)
        {
            foreach (var s in drawing.Symbols)
            {
                if (!symbols.TryGetValue(s.Kind, out var symbol) || symbol == null) continue; // already warned at resolve
                if (!symbol.IsActive) { symbol.Activate(); _doc.Regenerate(); }

                var inst = _doc.Create.NewFamilyInstance(Pt(s.Position), symbol, view);
                foreach (var kv in s.Params)
                {
                    var p = inst.LookupParameter(kv.Key);
                    if (p != null && !p.IsReadOnly) p.Set(kv.Value);
                    else result.Warnings.Add($"{s.Kind}: couldn't write \"{kv.Key}\".");
                }
                result.Symbols++;
            }
        }

        private void DrawWires(View view, DmxOneLineDrawing drawing, GraphicsStyle dashed, GraphicsStyle solid,
                               DmxOneLineResult result)
        {
            foreach (var w in drawing.Wires)
            {
                if (w.Start.X == w.End.X && w.Start.Y == w.End.Y) continue; // zero-length guard
                var dc = _doc.Create.NewDetailCurve(view, Line.CreateBound(Pt(w.Start), Pt(w.End)));
                var style = w.Dashed ? dashed : solid;
                if (style != null) { try { dc.LineStyle = style; } catch { /* style not applicable — leave default */ } }
                result.Wires++;
            }
        }

        private int DrawNotes(View view, IReadOnlyList<DmxNote> notes, ElementId textType, List<string> warnings)
        {
            if (textType == ElementId.InvalidElementId) { warnings.Add("No text type — notes skipped."); return 0; }
            int drawn = 0;
            foreach (var n in notes)
            {
                var opts = new TextNoteOptions(textType)
                {
                    HorizontalAlignment = Align(n.Align),
                    Rotation = 0.0,
                };
                TextNote.Create(_doc, view.Id, Pt(n.Position), n.Text, opts);
                drawn++;
            }
            return drawn;
        }

        private int DrawMarkers(View view, IReadOnlyList<DmxMarker> markers, FamilySymbol marker, List<string> warnings)
        {
            if (marker == null) return 0;
            if (!marker.IsActive) { marker.Activate(); _doc.Regenerate(); }
            int drawn = 0;
            foreach (var m in markers)
            {
                var inst = _doc.Create.NewFamilyInstance(Pt(m.Position), marker, view);
                var p = inst.LookupParameter(DmxOneLineGeometry.Marker.NumberParam);
                if (p != null && !p.IsReadOnly) p.Set(m.Mark);
                drawn++;
            }
            return drawn;
        }

        // ── Resolution helpers ───────────────────────────────────────────────────────────────────────
        private IReadOnlyDictionary<DmxSymbolKind, FamilySymbol> ResolveSymbols(DmxOneLineResult result)
        {
            var map = new Dictionary<DmxSymbolKind, FamilySymbol>();
            void Add(DmxSymbolKind kind, string family, string type)
            {
                var s = ResolveSymbol(family, type);
                if (s == null) result.Warnings.Add($"{kind} family \"{family}\" not loaded — skipped.");
                else map[kind] = s;
            }
            Add(DmxSymbolKind.Decoder, DmxOneLineGeometry.Decoder.Family, DmxOneLineGeometry.Decoder.Type);
            Add(DmxSymbolKind.Driver, DmxOneLineGeometry.Driver.Family, DmxOneLineGeometry.Driver.Type);
            Add(DmxSymbolKind.Interface, DmxOneLineGeometry.Interface.Family, DmxOneLineGeometry.Interface.Type);
            Add(DmxSymbolKind.Processor, DmxOneLineGeometry.Processor.Family, DmxOneLineGeometry.Processor.Type);
            Add(DmxSymbolKind.Terminator, DmxOneLineGeometry.Terminator.Family, DmxOneLineGeometry.Terminator.Type);
            return map;
        }

        private FamilySymbol ResolveSymbol(string family, string type) =>
            new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(s => string.Equals(s.FamilyName, family, StringComparison.Ordinal)
                                     && string.Equals(s.Name, type, StringComparison.Ordinal));

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
            double target = DmxOneLineGeometry.NoteTextHeightFt;
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
                if (baseType.Duplicate("TurboDMX 1-16in") is TextNoteType dup)
                {
                    dup.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(target);
                    return dup.Id;
                }
            }
            catch { /* duplicate name clash or locked — fall back to the base type below */ }
            return baseType.Id;
        }

        private static HorizontalTextAlignment Align(DmxTextAlign a) => a switch
        {
            DmxTextAlign.Right => HorizontalTextAlignment.Right,
            DmxTextAlign.Center => HorizontalTextAlignment.Center,
            _ => HorizontalTextAlignment.Left,
        };

        private static XYZ Pt(XY p) => new XYZ(p.X, p.Y, 0.0);

        private static void ReportWarnings(DmxOneLineResult result) => ReportWarnings(result.Warnings);
        private static void ReportWarnings(DmxWireLegendResult result) => ReportWarnings(result.Warnings);

        private static void ReportWarnings(List<string> warnings)
        {
            if (warnings.Count == 0) return;
            TaskDialog.Show("TurboDMX", string.Join("\n", warnings.Take(12)));
        }
    }
}
