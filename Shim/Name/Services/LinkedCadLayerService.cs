#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Name.Services;

/// <summary>
/// One layer row folded from Revit's VG → Imported Categories for a linked DWG: the file it belongs to,
/// the layer (subcategory) name, its live subcategory <see cref="ElementId"/>, and whether it is currently
/// hidden in the locked view. <see cref="SubId"/> is a live host-document subcategory id (never persisted).
/// </summary>
public sealed record CadLayerInfo(string FileName, string LayerName, ElementId SubId, bool Hidden);

/// <summary>
/// Folds the VG → Imported Categories checklist into TurboName. Imported-DWG layers are host-document
/// subcategories under each linked <see cref="ImportInstance"/>'s <see cref="Category"/> (spike-proven on
/// TEST LONGORIA: all 52 layers reported <c>CanCategoryBeHidden</c> + <c>AllowsVisibilityControl</c> true,
/// and both <c>Set/GetCategoryHidden</c> tracked the live VG checkbox). This lets TurboName list and toggle
/// layer visibility directly — instantly from subcategories, with no ACadSharp DWG read.
/// </summary>
public static class LinkedCadLayerService
{
    /// <summary>
    /// Every layer of every linked CAD import visible in <paramref name="view"/>, grouped-friendly by file
    /// (the import's category name — what VG shows), naturally sorted. Deduped by subcategory id so a DWG
    /// linked more than once contributes each layer once. Read-only; call from a valid API context.
    /// </summary>
    public static List<CadLayerInfo> Build(Document doc, View view)
    {
        var rows = new List<CadLayerInfo>();
        var seen = new HashSet<ElementId>();

        foreach (var import in CadLinkResolver.GetLinkedImports(doc, view))
        {
            var cat = import.Category;
            if (cat == null) continue;
            string fileName = cat.Name;

            foreach (Category sub in cat.SubCategories)
            {
                if (sub == null || !seen.Add(sub.Id)) continue;
                bool hidden = false;
                try { hidden = view.GetCategoryHidden(sub.Id); } catch { /* leave visible */ }
                rows.Add(new CadLayerInfo(fileName, sub.Name, sub.Id, hidden));
            }
        }

        var natural = new NaturalStringComparer();
        return rows
            .OrderBy(r => r.FileName, natural)
            .ThenBy(r => r.LayerName, natural)
            .ToList();
    }

    /// <summary>
    /// Show/hide one layer (subcategory) in the view. No-op when the category can't be controlled there, so
    /// an odd view never throws. Caller owns the transaction (mirrors <c>ToposolidVisibilityService</c>).
    /// </summary>
    public static void ApplyHidden(View view, ElementId subId, bool hidden)
    {
        if (view == null || subId == null) return;
        if (!view.CanCategoryBeHidden(subId)) return;
        view.SetCategoryHidden(subId, hidden);
    }
}
