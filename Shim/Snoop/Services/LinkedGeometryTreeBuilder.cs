#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Snoop.Models;

namespace TurboSuite.Snoop.Services;

/// <summary>
/// Builds the TurboSnoop report for a picked linked element, grouped to mirror the Visibility/Graphics dialog:
/// Category → Subcategory (both real VG checkboxes), regardless of which nested family the geometry came from
/// (nesting is aggregated away — the user cares about the checkbox, not the family tree). Read-only throughout.
///
/// Two passes, because the two content types surface differently:
///   1. MODEL geometry — never view-culled, so one viewless get_Geometry pass is complete.
///   2. VIEW-DEPENDENT / annotation (detail items, masking, symbolic lines) — visibility-filtered per view, so
///      a single view is unreliable (in testing a clearance line hid in 46 of 58 plan views). Sweep every plan
///      view and union, keeping only checkboxes the model pass missed. Do NOT collapse this to one view.
///
/// REJECTED ALTERNATIVES — do NOT re-attempt. The hard case is non-shared nested annotation (e.g. an
/// "x_DI_Clearance" Detail Item nested in a PL_Sink): it has no element/reference identity, so PickObject
/// (returns REFERENCE_TYPE_NONE → the whole top element), GetSubComponentIds (empty), and EditFamily (throws —
/// linked docs are read-only) all fail. Reading GraphicsStyleId off RENDERED geometry (what this builder does)
/// is the only mechanism that reaches it, precisely because it needs no reference.
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
        // Root = the family's category (the family name/type goes in the window header), so the tree matches VG.
        var root = new SnoopNode(rootCategory ?? "(no category)", SnoopNodeKind.Family);

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

        var modelSection = new SnoopNode("Model geometry", SnoopNodeKind.Info);
        root.Children.Add(modelSection);
        BuildGroupedSection(modelSection, modelPairs);

        // ── 2. View-dependent / annotation: sweep plan views, keep only what the model pass missed. ──
        var annoPairs = new HashSet<Checkbox>();
        var views = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(v => !v.IsTemplate)
            .ToList();

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

                var viewPairs = new HashSet<Checkbox>();
                CollectPairs(g, doc, rootCategory, viewPairs, 0);
                foreach (Checkbox cb in viewPairs)
                    if (!modelPairs.Contains(cb))
                        annoPairs.Add(cb);
            }
            catch { }
        }

        var annoSection = new SnoopNode("View-dependent / annotation", SnoopNodeKind.Info);
        root.Children.Add(annoSection);
        BuildGroupedSection(annoSection, annoPairs);

        return root;
    }

    /// <summary>Groups checkboxes into Category nodes with Subcategory children, ordered by name.</summary>
    private static void BuildGroupedSection(SnoopNode section, IEnumerable<Checkbox> checkboxes)
    {
        var byCategory = checkboxes
            .GroupBy(cb => cb.Category)
            .OrderBy(g => g.Key);

        bool any = false;
        foreach (var catGroup in byCategory)
        {
            any = true;
            var catNode = new SnoopNode(catGroup.Key, SnoopNodeKind.Category);
            section.Children.Add(catNode);

            foreach (Checkbox cb in catGroup
                         .Where(c => c.Subcategory != null)
                         .OrderBy(c => c.Subcategory))
            {
                catNode.Children.Add(new SnoopNode(cb.Subcategory, SnoopNodeKind.Subcategory));
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

    /// <summary>The picked family's "FamilyName : Type" label for the window header (no category prefix —
    /// the category is the tree's root node).</summary>
    public static string DescribeFamily(Element e)
    {
        if (e == null)
            return "(null element)";
        string fam = (e as FamilyInstance)?.Symbol?.FamilyName;
        return fam != null ? $"{fam} : {e.Name}" : e.Name;
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
