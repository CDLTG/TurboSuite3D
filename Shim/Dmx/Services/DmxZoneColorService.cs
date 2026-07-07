#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Dmx;
using TurboSuite.Dmx.Overlay;
using TurboSuite.Dmx.Services;

namespace TurboSuite.Dmx.Services
{
    /// <summary>
    /// Shim-side <see cref="IDmxZoneColorService"/> — the Control-Zone color overlay. While the
    /// TurboDMX window is open, the active view's DMX fixtures are colored by their <c>Control Zone</c> so the
    /// designer can tell zones apart; on close the overlay reverts and the view looks as it did before.
    ///
    /// Mechanism: <b>direct per-element overrides</b> (<see cref="View.SetElementOverrides"/>) — the same thing
    /// as the UI's "Override Graphics in View ▸ By Element". We tried named <c>ParameterFilterElement</c>s
    /// first (cleaner legend, self-healing sweep) but hit "parameter does not apply to this filter's
    /// categories": the firm's <c>Control Zone</c> is bound such that it's READABLE (Properties / the loop
    /// reader) but NOT filterable — confirmed by manual testing that a filter rule on it only works once the
    /// param is added as a <b>Project Parameter</b> (the Manage ▸ Project Parameters binding is what registers
    /// it with the filter system). On top of that, view templates blunt-blocked the filter path. Per-element
    /// overrides sidestep both: no filter rule (the param only has to be READABLE — it's what the reader groups
    /// loops by), and element overrides aren't template-controlled, so they apply under a template. Decision
    /// (2026-06-29): keep per-element even though making Control Zone a project parameter would likely revive
    /// filters — it's the more robust fit for a transient overlay and doesn't depend on the template's binding.
    /// Trade-off vs. filters: no V/G legend, and a leaked set after a crash has no name marker to sweep — but
    /// the overlay is deterministic per fixture, so the next open re-applies and the next clean close clears.
    ///
    /// We remember which elements we colored (per view) so <see cref="Revert"/> clears exactly those. There is
    /// no transient coloring in Revit, so each apply/revert dirties the doc and pushes one undo entry —
    /// accepted cost.
    /// </summary>
    public sealed class DmxZoneColorService : IDmxZoneColorService
    {
        private const int OverlayLineWeight = 6;   // 1–16; bold so the colored zone symbol stands out
        private readonly UIDocument _uidoc;
        // View id → the fixtures we overrode in it, so Revert (and a re-Apply) clears exactly our additions.
        private readonly Dictionary<ElementId, List<ElementId>> _appliedByView =
            new Dictionary<ElementId, List<ElementId>>();

        public DmxZoneColorService(UIDocument uidoc)
        {
            _uidoc = uidoc;
        }

        public string Apply(IReadOnlyDictionary<string, DmxColor> zoneColors)
        {
            Document doc = _uidoc?.Document;
            View view = doc?.ActiveView;
            if (doc == null || view == null || zoneColors == null || zoneColors.Count == 0) return "";

            if (!SupportsElementOverrides(view))
                return "Zone colors skipped — this view type can't show per-element graphic overrides.";

            // Map the fixtures VISIBLE in this view to their zone color, reading Control Zone the same way the
            // reader does (instance param, AsString). This is the read we know works — loops group on it.
            var fixtures = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_LightingFixtures)
                .WhereElementIsNotElementType()
                .ToElements();

            var toColor = new List<KeyValuePair<ElementId, DmxColor>>();
            foreach (var fi in fixtures)
            {
                string zone = fi.LookupParameter(DmxParameterNames.ControlZone)?.AsString()?.Trim();
                if (!string.IsNullOrEmpty(zone) && zoneColors.TryGetValue(zone, out var col))
                    toColor.Add(new KeyValuePair<ElementId, DmxColor>(fi.Id, col));
            }
            if (toColor.Count == 0)
                return "Zone colors skipped — no Control-Zone fixtures are visible in the active view.";

            ElementId solidFill = FindSolidFillPatternId(doc);

            using (var tx = new Transaction(doc, "TurboDMX — color zones"))
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

            using (var tx = new Transaction(doc, "TurboDMX — revert zone colors"))
            {
                tx.Start();
                foreach (var viewId in _appliedByView.Keys.ToList())
                    ClearView(doc, doc.GetElement(viewId) as View);
                tx.Commit();
            }
            _appliedByView.Clear();
            return "";
        }

        /// <summary>Clear the overrides WE applied to <paramref name="view"/> (reset to a blank
        /// <see cref="OverrideGraphicSettings"/>), then forget them. Only touches the fixtures we colored, so
        /// the user's own per-element overrides elsewhere are left alone.</summary>
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

        private static OverrideGraphicSettings MakeOverride(DmxColor c, ElementId solidFill)
        {
            var color = new Color(c.R, c.G, c.B);
            var ogs = new OverrideGraphicSettings();
            // Color projection AND cut, lines + solid fills — fixtures in plan/RCP draw mostly as symbolic
            // lines (no surface), so line color is what shows there; the fills cover 3D/section and any
            // fixture that renders a face. Line weight bumped to 6 so the colored symbol reads boldly.
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
