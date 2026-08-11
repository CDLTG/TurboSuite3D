#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Fills the Panel Breakdown's capacity bars: builds one <see cref="ProcessorInstance"/> per placed
    /// "Processor" compartment slot, runs <see cref="ControlLinkPacker"/> against the links those
    /// processors provide, and writes the result onto each instance's <see cref="ProcessorLink"/>.
    ///
    /// This used to <i>be</i> the link math — a forward-only first-fit that was the third independent
    /// computation of QS-link demand in the codebase, and disagreed with the other two. All it does
    /// now is adapt: build links, pack, then map positionally onto Link 1, Link 2, Link 3… Because the
    /// packer returns QS links first and Clear Connect last, the trailing links are the wireless ones,
    /// which is the behaviour the display has always had.
    ///
    /// <b>Per slot, not per panel.</b> Processors are counted the same way the BOM's supply sizer counts
    /// them — one per "Processor" compartment selection — so an LV21 with a processor in each compartment
    /// is two instances and four link bars, and the bars agree with the two supplies the BOM orders. A
    /// <see cref="PanelResult"/> could not carry that: it has a single Link1/Link2 pair.
    /// </summary>
    public static class LinkAssignmentService
    {
        /// <summary>
        /// Builds the sidebar's processor instances and packs the job onto their links.
        ///
        /// <paramref name="extras"/> is the same object the BOM is built from, deliberately: the bars
        /// and the processor recommendation must be derived from identical inputs, or they can drift
        /// apart through the inputs even while sharing the algorithm.
        /// </summary>
        public static List<ProcessorInstance> BuildProcessorInstances(
            List<PanelResult> allPanels, BomExtras extras, BrandConfig brand = null)
        {
            var instances = new List<ProcessorInstance>();
            if (allPanels == null) return instances;

            // One instance per "Processor" compartment slot, in panel order. A panel holding more than
            // one gets its instances suffixed "(1)", "(2)" so the two LV21 processors are distinct.
            foreach (var panel in allPanels)
            {
                int procSlots = panel.CompartmentSlots.Count(IsProcessorSlot);
                if (procSlots == 0) continue;

                int n = 0;
                foreach (var slot in panel.CompartmentSlots)
                {
                    if (!IsProcessorSlot(slot)) continue;
                    n++;
                    instances.Add(new ProcessorInstance
                    {
                        PanelName = panel.PanelName,
                        Label = procSlots > 1 ? $"{panel.PanelName} ({n})" : panel.PanelName,
                        Link1 = new ProcessorLink { ProcessorPanelName = panel.PanelName, LinkNumber = 1 },
                        Link2 = new ProcessorLink { ProcessorPanelName = panel.PanelName, LinkNumber = 2 }
                    });
                }
            }

            if (instances.Count == 0) return instances;

            var links = new List<ProcessorLink>();
            foreach (var inst in instances)
            {
                links.Add(inst.Link1);
                links.Add(inst.Link2);
            }

            // Brand rides along so a compartment device's nameplate legs (QSE-IO → 5) show on the bars.
            // PDU is computed too but nothing here reads it — only the BOM's supply sizer does.
            var packed = ControlLinkPacker.Pack(
                ControlLinkPacker.BuildDemand(allPanels, extras, brand), links.Count);

            for (int i = 0; i < links.Count; i++)
            {
                var result = i < packed.Links.Count ? packed.Links[i] : null;

                // Type first: it is what decides a link's capacity, so the over-capacity flags raised
                // by the two setters below are only correct once it is set.
                links[i].LinkType = result?.LinkType ?? ProcessorLink.QsLinkType;
                links[i].UsedDevices = result?.Devices ?? 0;
                links[i].UsedLoads = result?.Loads ?? 0;
                links[i].UsedRepeaters = result?.Repeaters ?? 0;
            }

            return instances;
        }

        private static bool IsProcessorSlot(string slot)
            => string.Equals(slot, "Processor", StringComparison.OrdinalIgnoreCase);
    }
}
