#nullable enable
using System;
using System.Collections.Generic;
using TurboSuite.Shared.Constants;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.OneLine
{
    /// <summary>
    /// Resolves the Panel Breakdown's <see cref="PanelResult"/>s + the job <see cref="BrandConfig"/> into the
    /// name → <see cref="ControlPanelRenderData"/> lookup the one-line planner joins against. Pure Core (no
    /// Revit) so it is testable and the planner stays free of the panel model. Shade panels need no entry —
    /// the planner renders them straight off their <c>PackedLinkUnit</c>.
    /// </summary>
    public static class ControlRenderDataFactory
    {
        public static IReadOnlyDictionary<string, ControlPanelRenderData> BuildPanels(
            IReadOnlyList<PanelResult> allPanels, BrandConfig? brand)
        {
            var map = new Dictionary<string, ControlPanelRenderData>(StringComparer.Ordinal);
            if (allPanels == null) return map;

            foreach (var p in allPanels)
            {
                if (string.IsNullOrEmpty(p.PanelName) || map.ContainsKey(p.PanelName)) continue;

                string partNumber =
                    brand?.PanelPartNumbers != null && brand.PanelPartNumbers.TryGetValue(p.PanelCapacity, out var pn)
                        ? pn : $"PD{p.PanelCapacity}";

                // Module tiles TOP→BOTTOM. Panels fill bottom-up (module 1 at the bottom, just above the LV
                // compartment; empties at the top) — matching the Panel Breakdown (584) and the physical DIN
                // fill. TileCenterY draws index 0 at the top, so: a null per empty slot first, then the modules
                // in the Panel Breakdown's bottom-up order (module N…1, so module 1 lands at the bottom).
                var tiles = new List<string?>();
                for (int i = 0; i < p.EmptySlots; i++) tiles.Add(null);
                foreach (var m in p.VisibleModulesBottomUp)
                    tiles.Add(string.IsNullOrEmpty(m.PartNumber) ? m.TypeLabel : m.PartNumber);

                // LV compartment labels (PD8/PD9 = 1, LV21 = 2): the selected device's part number, or EMPTY.
                var lvSlots = new List<string>();
                foreach (var slot in p.CompartmentSlots)
                    lvSlots.Add(LvLabel(slot, p.SpecialDevicePartNumbers));

                string enclosureRole = p.HasDualSpecialCompartment
                    ? Roles.ControlLv21Detail : Roles.ControlPanelDetail;

                map[p.PanelName] = new ControlPanelRenderData(
                    p.PanelName, partNumber, $"{p.TotalModuleCount}/{p.PanelCapacity}",
                    tiles, lvSlots, p.IsProcessor, enclosureRole);
            }
            return map;
        }

        private static string LvLabel(string slot, IReadOnlyDictionary<string, string> partNumbers)
        {
            if (string.IsNullOrWhiteSpace(slot) || string.Equals(slot, "Empty", StringComparison.OrdinalIgnoreCase))
                return "EMPTY";
            if (partNumbers != null && partNumbers.TryGetValue(slot, out var pn) && !string.IsNullOrEmpty(pn))
                return pn;
            return slot;
        }
    }
}
