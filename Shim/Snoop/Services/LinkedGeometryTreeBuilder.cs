#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Snoop.Models;

namespace TurboSuite.Snoop.Services;

/// <summary>
/// Builds the TurboSnoop report for a picked linked element, organised to mirror the Visibility/Graphics
/// dialog the user actually clicks: each section groups geometry by <b>Category → Subcategory</b> (both of
/// which are real VG checkboxes), regardless of which nested family the geometry came from.
///
/// Two sections, because the spike proved two content types exist:
///   1. MODEL geometry (solids, model lines, nested model families) — one viewless pass is complete; model
///      geometry is never view-culled. This is where "real geometry" lands: a glass pane on a Glass
///      subcategory, an elevator's mechanical-part subcategories, etc.
///   2. VIEW-DEPENDENT / annotation (detail items, masking regions, symbolic lines) — only appears through a
///      view AND is visibility-filtered, so we sweep every plan view and keep the Category → Subcategory the
///      model pass did NOT already have, with a per-subcategory view count. This is where the clearance lines
///      (Detail Items → &lt;Hidden Lines&gt;) land.
///
/// Geometry from nested families is aggregated into the same Category → Subcategory grouping (the nesting
/// itself isn't shown — the user cares about the checkbox, not the family tree). All read-only; linked docs
/// are read-only.
///
/// WHY SWEEP MANY VIEWS (do NOT "optimize" the annotation pass to a single view): get_Geometry(Options.View)
/// returns only what is VISIBLE in that view, and annotation is visibility-filtered — in the validating test
/// the clearance hid in 46 of 58 plan views and surfaced in only 12. A single "best view" is unreliable; the
/// sweep-then-union is load-bearing. (The model pass needs no view and is complete in one extraction.)
///
/// REJECTED ALTERNATIVES — do NOT re-attempt. Spiked against a real arch link: a NON-shared nested
/// "x_DI_Clearance" Detail Item drawing on "Detail Items → &lt;Hidden Lines&gt;" inside a PL_Sink. All fail for
/// the same root cause — non-shared nested annotation has no element/reference identity:
///   • Click-the-line (PickObject(LinkedElement) → CreateReferenceInLink → GetGeometryObjectFromReference):
///     the pick returns REFERENCE_TYPE_NONE and resolves to the whole top element, never the line. No pick
///     mode (incl. PointOnElement) can return a reference to something that has none.
///   • GetSubComponentIds(): empty — the nest is non-shared.
///   • EditFamily(): throws — linked documents are read-only.
///   • RevitLookup "Snoop Linked Element": resolves the element exactly as we do and hits the same wall.
/// Reading GraphicsStyleId off RENDERED geometry (what this builder does) is the ONLY mechanism that reaches
/// such content, precisely because it never needs a reference.
/// </summary>
public sealed class LinkedGeometryTreeBuilder
{
    private const int MaxDepth = 12;

    // Revit's internal GraphicsStyle parent (cut geometry, invisible lines) — never a real VG checkbox.
    private const string InternalStylesCategory = "Internal Object Styles";

    // A VG checkbox: a category, plus an optional subcategory (null = geometry drawn directly in the category).
    private readonly struct Checkbox
    {
        public readonly string Category;
        public readonly string Subcategory;
        public Checkbox(string category, string subcategory) { Category = category; Subcategory = subcategory; }
    }

    public SnoopNode BuildUnion(Element linkedElement, Document doc)
    {
        string rootCategory = linkedElement.Category?.Name;
        var root = new SnoopNode(DescribeElement(linkedElement), SnoopNodeKind.Family);

        // ── 1. Model geometry: one viewless pass. ──
        var modelPairs = new HashSet<Checkbox>();
        try
        {
            GeometryElement modelGeom = linkedElement.get_Geometry(new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = true,
                DetailLevel = ViewDetailLevel.Fine,
            });
            if (modelGeom != null)
                CollectPairs(modelGeom, doc, rootCategory, modelPairs, 0);
        }
        catch { }

        var modelSection = new SnoopNode(
            "Model geometry (reliably present — solids, model lines, nested families):", SnoopNodeKind.Info);
        root.Children.Add(modelSection);
        BuildGroupedSection(modelSection, modelPairs.Select(p => new KeyValuePair<Checkbox, int>(p, 0)), false);

        // ── 2. View-dependent / annotation: sweep plan views, keep only what the model pass missed. ──
        var annoCounts = new Dictionary<Checkbox, int>();
        var views = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(v => !v.IsTemplate)
            .ToList();

        int scanned = 0;
        foreach (ViewPlan v in views)
        {
            try
            {
                GeometryElement g = linkedElement.get_Geometry(new Options
                {
                    View = v,
                    ComputeReferences = false,
                    IncludeNonVisibleObjects = true,
                });
                if (g == null) continue;

                scanned++;
                var viewPairs = new HashSet<Checkbox>();
                CollectPairs(g, doc, rootCategory, viewPairs, 0);
                foreach (Checkbox cb in viewPairs)
                    if (!modelPairs.Contains(cb))
                        annoCounts[cb] = annoCounts.TryGetValue(cb, out int c) ? c + 1 : 1;
            }
            catch { }
        }

        var annoSection = new SnoopNode(
            $"View-dependent / annotation (in some of {scanned} views — detail items, masking, symbolic lines):",
            SnoopNodeKind.Info);
        root.Children.Add(annoSection);
        BuildGroupedSection(annoSection, annoCounts, true);

        return root;
    }

    /// <summary>Groups checkboxes into Category nodes with Subcategory children. When <paramref name="showCounts"/>
    /// is set, each subcategory is suffixed with the number of views it surfaced in.</summary>
    private static void BuildGroupedSection(SnoopNode section,
        IEnumerable<KeyValuePair<Checkbox, int>> entries, bool showCounts)
    {
        var byCategory = entries
            .GroupBy(e => e.Key.Category)
            .OrderBy(g => g.Key);

        bool any = false;
        foreach (var catGroup in byCategory)
        {
            any = true;
            var catNode = new SnoopNode(catGroup.Key, SnoopNodeKind.Category);
            section.Children.Add(catNode);

            foreach (var entry in catGroup
                         .Where(e => e.Key.Subcategory != null)
                         .OrderBy(e => e.Key.Subcategory))
            {
                string label = showCounts ? $"{entry.Key.Subcategory}   ({entry.Value}×)" : entry.Key.Subcategory;
                catNode.Children.Add(new SnoopNode(label, SnoopNodeKind.Subcategory));
            }
        }

        if (!any)
            section.Children.Add(new SnoopNode("(none)", SnoopNodeKind.Info));
    }

    private void CollectPairs(GeometryElement geom, Document doc, string contextCategory,
        HashSet<Checkbox> into, int depth)
    {
        if (depth > MaxDepth)
            return;

        foreach (GeometryObject go in geom)
        {
            if (go is GeometryInstance inst)
            {
                // Recurse into nested geometry (and the element's own symbol wrapper). Geometry with no
                // subcategory style draws in its owning family's category, so carry that as the context.
                Element symbol = ResolveSymbol(inst, doc);
                string ctx = symbol?.Category?.Name ?? contextCategory;

                GeometryElement nested = inst.GetInstanceGeometry();
                if (nested != null)
                    CollectPairs(nested, doc, ctx, into, depth + 1);
            }
            else
            {
                Checkbox? cb = ResolveCheckbox(go, doc, contextCategory);
                if (cb.HasValue)
                    into.Add(cb.Value);
            }
        }
    }

    private static Checkbox? ResolveCheckbox(GeometryObject go, Document doc, string contextCategory)
    {
        if (go is Solid solid && solid.Faces.Size == 0 && solid.Edges.Size == 0)
            return null;

        ElementId styleId = go.GraphicsStyleId;
        if (styleId != null && styleId != ElementId.InvalidElementId &&
            doc.GetElement(styleId) is GraphicsStyle gs && gs.GraphicsStyleCategory is Category sub)
        {
            string parentName = sub.Parent?.Name;

            // Internal render styles (cut geometry, invisible lines) aren't real RVT-Links checkboxes.
            if (parentName == InternalStylesCategory || sub.Name == InternalStylesCategory)
                return null;

            // Subcategory (has a parent category) vs. a style pointing straight at a top-level category.
            return sub.Parent != null ? new Checkbox(parentName, sub.Name) : new Checkbox(sub.Name, null);
        }

        // No subcategory style = drawn in the owning family's main category → that category's checkbox.
        return contextCategory != null ? new Checkbox(contextCategory, null) : (Checkbox?)null;
    }

    private static string DescribeElement(Element e)
    {
        if (e == null)
            return "(null element)";
        string cat = e.Category?.Name ?? "?";
        string fam = (e as FamilyInstance)?.Symbol?.FamilyName;
        return fam != null ? $"{cat}: {fam} : {e.Name}" : $"{cat}: {e.Name}";
    }

    // GeometryInstance.Symbol was deprecated (Revit 2023+); resolve the owning symbol via the geometry id so
    // we can read its category (used as the "no-subcategory" context for nested geometry).
    private static Element ResolveSymbol(GeometryInstance inst, Document doc)
    {
        try
        {
            ElementId symbolId = inst.GetSymbolGeometryId().SymbolId;
            return symbolId != null && symbolId != ElementId.InvalidElementId ? doc.GetElement(symbolId) : null;
        }
        catch
        {
            return null;
        }
    }
}
