#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Dali.Overlay;
using TurboSuite.Dali.Services;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Dali.Services
{
    /// <summary>
    /// Shim-side <see cref="IDaliZoneColorService"/> — the DALI Control-Zone color overlay, a copy of
    /// <c>DmxZoneColorService</c> with <b>one change</b>: the colored set is scoped on
    /// <c>Dimming Protocol = DALI</c> instead of <c>= DMX</c>. That re-scope is not optional — DMX's service
    /// already scopes to DMX <i>specifically so it won't cross-color DALI fixtures sharing a zone name</i>;
    /// this DALI service scoping to DALI is the symmetric half that lets both overlays coexist.
    ///
    /// Mechanism (verbatim from DMX): direct per-element overrides (<see cref="View.SetElementOverrides"/>) on
    /// the ACTIVE view — works under a view template (unlike filters), and the firm's <c>Control Zone</c>
    /// shared param only has to be READABLE (it's what loops group on), not filterable. We remember which
    /// elements we colored (per view) so <see cref="Revert"/> clears exactly those, leaving the user's own
    /// overrides alone. There is no transient coloring in Revit, so each apply/revert dirties the doc and
    /// pushes one undo entry — the accepted DMX cost.
    /// </summary>
    public sealed class DaliZoneColorService : IDaliZoneColorService
    {
        private const int OverlayLineWeight = 6;   // 1–16; bold so the colored zone symbol stands out
        private readonly UIDocument _uidoc;
        // View id → the fixtures we overrode in it, so Revert (and a re-Apply) clears exactly our additions.
        private readonly Dictionary<ElementId, List<ElementId>> _appliedByView =
            new Dictionary<ElementId, List<ElementId>>();

        public DaliZoneColorService(UIDocument uidoc)
        {
            _uidoc = uidoc;
        }

        public string Apply(IReadOnlyDictionary<string, DaliColor> zoneColors)
        {
            Document doc = _uidoc?.Document;
            View view = doc?.ActiveView;
            if (doc == null || view == null || zoneColors == null || zoneColors.Count == 0) return "";

            if (!SupportsElementOverrides(view))
                return "Zone colors skipped — this view type can't show per-element graphic overrides.";

            var fixtures = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_LightingFixtures)
                .WhereElementIsNotElementType()
                .ToElements();

            var toColor = new List<KeyValuePair<ElementId, DaliColor>>();
            foreach (var fi in fixtures)
            {
                // Only color DALI fixtures — Control Zone is shared with DMX, so an unscoped value match would
                // cross-color a DMX loop sharing a zone name. Scope on Dimming Protocol, the DALI membership rule.
                if (!(fi is FamilyInstance inst)) continue;
                if (!ParameterHelper.GetDimmingProtocol(inst).Trim()
                        .Equals("DALI", StringComparison.OrdinalIgnoreCase)) continue;

                string zone = fi.LookupParameter(ParameterNames.ControlZone)?.AsString()?.Trim();
                if (!string.IsNullOrEmpty(zone) && zoneColors.TryGetValue(zone, out var col))
                    toColor.Add(new KeyValuePair<ElementId, DaliColor>(fi.Id, col));
            }
            if (toColor.Count == 0)
                return "Zone colors skipped — no DALI Control-Zone fixtures are visible in the active view.";

            ElementId solidFill = FindSolidFillPatternId(doc);

            using (var tx = new Transaction(doc, "TurboDALI — color zones"))
            {
                tx.Start();
                ClearView(doc, view);   // reconcile within the session before re-applying

                var applied = new List<ElementId>(toColor.Count);
                foreach (var kvp in toColor)
                {
                    view.SetElementOverrides(kvp.Key, MakeOverride(kvp.Value, solidFill));
                    applied.Add(kvp.Key);
                }
                _appliedByView[view.Id] = applied;
                tx.Commit();
            }
            return "";
        }

        public string Revert()
        {
            Document doc = _uidoc?.Document;
            if (doc == null || _appliedByView.Count == 0) return "";

            using (var tx = new Transaction(doc, "TurboDALI — revert zone colors"))
            {
                tx.Start();
                foreach (var viewId in _appliedByView.Keys.ToList())
                    ClearView(doc, doc.GetElement(viewId) as View);
                tx.Commit();
            }
            _appliedByView.Clear();
            return "";
        }

        /// <summary>Clear the overrides WE applied to <paramref name="view"/>, then forget them. Only touches
        /// the fixtures we colored, so the user's own per-element overrides elsewhere are left alone.</summary>
        private void ClearView(Document doc, View view)
        {
            if (view == null) { return; }
            if (!_appliedByView.TryGetValue(view.Id, out var ids)) return;

            var blank = new OverrideGraphicSettings();
            foreach (var id in ids)
                if (doc.GetElement(id) != null)
                    view.SetElementOverrides(id, blank);
            _appliedByView.Remove(view.Id);
        }

        private static OverrideGraphicSettings MakeOverride(DaliColor c, ElementId solidFill)
        {
            var color = new Color(c.R, c.G, c.B);
            var ogs = new OverrideGraphicSettings();
            ogs.SetProjectionLineColor(color);
            ogs.SetCutLineColor(color);
            ogs.SetProjectionLineWeight(OverlayLineWeight);
            ogs.SetCutLineWeight(OverlayLineWeight);
            ogs.SetSurfaceForegroundPatternVisible(true);
            ogs.SetSurfaceForegroundPatternColor(color);
            ogs.SetCutForegroundPatternVisible(true);
            ogs.SetCutForegroundPatternColor(color);
            if (solidFill != null && solidFill != ElementId.InvalidElementId)
            {
                ogs.SetSurfaceForegroundPatternId(solidFill);
                ogs.SetCutForegroundPatternId(solidFill);
            }
            return ogs;
        }

        /// <summary>Per-element overrides apply on graphical views (even under a view template); schedules,
        /// sheets, legends and the like can't show them.</summary>
        private static bool SupportsElementOverrides(View view)
        {
            switch (view.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.EngineeringPlan:
                case ViewType.AreaPlan:
                case ViewType.Section:
                case ViewType.Elevation:
                case ViewType.Detail:
                case ViewType.ThreeD:
                case ViewType.DraftingView:
                    return true;
                default:
                    return false;
            }
        }

        private static ElementId FindSolidFillPatternId(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern()?.IsSolidFill == true)?.Id;
        }
    }
}
