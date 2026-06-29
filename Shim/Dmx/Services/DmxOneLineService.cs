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
                    DrawNotes(view, drawing, textType, result);
                    DrawMarkers(view, drawing, marker, result);

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

        // ── View ownership: registry id → by name → create ───────────────────────────────────────────
        private View FindOrCreateView(DmxOneLineDrawing drawing, string systemName,
                                      IReadOnlyDictionary<int, long> viewRegistry, DmxOneLineResult result)
        {
            string name = drawing.ViewName(systemName);

            if (viewRegistry != null && viewRegistry.TryGetValue(drawing.InterfaceNumber, out long vid) && vid != 0L)
            {
                if (_doc.GetElement(new ElementRef(vid).ToElementId()) is ViewDrafting existing && !existing.IsTemplate)
                    return existing;
            }

            var byName = new FilteredElementCollector(_doc).OfClass(typeof(ViewDrafting)).Cast<ViewDrafting>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, name, StringComparison.Ordinal));
            if (byName != null) return byName;

            var vft = new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.Drafting);
            if (vft == null) { result.Warnings.Add("No Drafting view type in the project — cannot create the one-line view."); return null; }

            var view = ViewDrafting.Create(_doc, vft.Id);
            TrySetName(view, name);
            try { view.Scale = DmxOneLineGeometry.ViewScale; } catch { /* some templates lock scale */ }
            result.Created = true;
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

        private void DrawNotes(View view, DmxOneLineDrawing drawing, ElementId textType, DmxOneLineResult result)
        {
            if (textType == ElementId.InvalidElementId) { result.Warnings.Add("No text type — notes skipped."); return; }
            foreach (var n in drawing.Notes)
            {
                var opts = new TextNoteOptions(textType)
                {
                    HorizontalAlignment = Align(n.Align),
                    Rotation = 0.0,
                };
                TextNote.Create(_doc, view.Id, Pt(n.Position), n.Text, opts);
                result.Notes++;
            }
        }

        private void DrawMarkers(View view, DmxOneLineDrawing drawing, FamilySymbol marker, DmxOneLineResult result)
        {
            if (marker == null) return;
            if (!marker.IsActive) { marker.Activate(); _doc.Regenerate(); }
            foreach (var m in drawing.Markers)
            {
                var inst = _doc.Create.NewFamilyInstance(Pt(m.Position), marker, view);
                var p = inst.LookupParameter(DmxOneLineGeometry.Marker.NumberParam);
                if (p != null && !p.IsReadOnly) p.Set(m.Mark);
                result.Markers++;
            }
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

        private static void ReportWarnings(DmxOneLineResult result)
        {
            if (result.Warnings.Count == 0) return;
            TaskDialog.Show("TurboDMX — One-line", string.Join("\n", result.Warnings.Take(12)));
        }
    }
}
