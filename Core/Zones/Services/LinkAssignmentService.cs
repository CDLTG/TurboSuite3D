#nullable disable
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Fills the Panel Breakdown's capacity bars: runs <see cref="ControlLinkPacker"/> against the
    /// links the sited processors actually provide, and writes the result onto each
    /// <see cref="ProcessorLink"/>.
    ///
    /// This used to <i>be</i> the link math — a forward-only first-fit that was the third independent
    /// computation of QS-link demand in the codebase, and disagreed with the other two. All it does
    /// now is adapt: pack, then map positionally onto Link 1, Link 2, Link 3… Because the packer
    /// returns QS links first and Clear Connect last, the trailing links are the wireless ones, which
    /// is the behaviour the display has always had.
    /// </summary>
    public static class LinkAssignmentService
    {
        /// <summary>
        /// Packs the job onto the sited processors' links and updates their bars in place.
        ///
        /// <paramref name="extras"/> is the same object the BOM is built from, deliberately: the bars
        /// and the processor recommendation must be derived from identical inputs, or they can drift
        /// apart through the inputs even while sharing the algorithm.
        /// </summary>
        public static void AssignAndAggregate(List<PanelResult> allPanels, BomExtras extras)
        {
            if (allPanels == null) return;

            var processorPanels = allPanels.Where(p => p.IsProcessor).ToList();
            if (processorPanels.Count == 0) return;

            var links = new List<ProcessorLink>();
            foreach (var proc in processorPanels)
            {
                if (proc.Link1 == null)
                    proc.Link1 = new ProcessorLink { ProcessorPanelName = proc.PanelName, LinkNumber = 1 };
                if (proc.Link2 == null)
                    proc.Link2 = new ProcessorLink { ProcessorPanelName = proc.PanelName, LinkNumber = 2 };

                links.Add(proc.Link1);
                links.Add(proc.Link2);
            }

            var packed = ControlLinkPacker.Pack(
                ControlLinkPacker.BuildDemand(allPanels, extras), links.Count);

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
        }
    }
}
