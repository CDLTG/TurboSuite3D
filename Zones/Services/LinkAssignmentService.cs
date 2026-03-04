#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    public static class LinkAssignmentService
    {
        /// <summary>
        /// Assigns panels to processor links and aggregates device/load counts.
        /// When wireless devices are present, the last link(s) are designated Clear Connect Type A
        /// and reserved exclusively for wireless devices (hybrid repeaters).
        /// QSE-IO and QSE-CI-DMX special compartment devices each count as 1 device on their link.
        /// </summary>
        public static void AssignAndAggregate(List<PanelResult> allPanels, int keypadCount, int hybridRepeaterCount = 0)
        {
            bool hasWirelessDevices = hybridRepeaterCount > 0;

            // Build processor links from panels that have Processor selected
            var processorPanels = allPanels
                .Where(p => p.HasSpecialCompartment
                    && string.Equals(p.SelectedSpecialDevice, "Processor", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (processorPanels.Count == 0)
                return;

            var allLinks = new List<ProcessorLink>();
            foreach (var proc in processorPanels)
            {
                if (proc.Link1 == null)
                    proc.Link1 = new ProcessorLink { ProcessorPanelName = proc.PanelName, LinkNumber = 1 };
                if (proc.Link2 == null)
                    proc.Link2 = new ProcessorLink { ProcessorPanelName = proc.PanelName, LinkNumber = 2 };

                // Reset link types to QS (will designate CC-A below if needed)
                proc.Link1.LinkType = "QS";
                proc.Link2.LinkType = "QS";

                allLinks.Add(proc.Link1);
                allLinks.Add(proc.Link2);
            }

            // Designate Clear Connect Type A links (last link first, working backward)
            var ccaLinks = new List<ProcessorLink>();
            if (hasWirelessDevices && allLinks.Count > 0)
            {
                int ccaLinksNeeded = (int)Math.Ceiling((double)hybridRepeaterCount / ProcessorLink.MaxDevices);
                ccaLinksNeeded = Math.Max(ccaLinksNeeded, 1);

                // Assign CC-A from the last link backward
                for (int i = allLinks.Count - 1; i >= 0 && ccaLinks.Count < ccaLinksNeeded; i--)
                {
                    allLinks[i].LinkType = "Clear Connect Type A";
                    ccaLinks.Add(allLinks[i]);
                }
            }

            // Separate QS links from CC-A links
            var qsLinks = allLinks.Where(l => !l.IsClearConnect).ToList();

            // Track accumulated devices and loads per link
            var linkDevices = new Dictionary<ProcessorLink, int>();
            var linkLoads = new Dictionary<ProcessorLink, int>();
            foreach (var link in allLinks)
            {
                linkDevices[link] = 0;
                linkLoads[link] = 0;
            }

            // Assign hybrid repeaters to CC-A link(s)
            if (hybridRepeaterCount > 0 && ccaLinks.Count > 0)
            {
                int remaining = hybridRepeaterCount;
                foreach (var link in ccaLinks)
                {
                    if (remaining <= 0) break;
                    int assign = Math.Min(remaining, ProcessorLink.MaxDevices);
                    linkDevices[link] += assign;
                    remaining -= assign;
                }
            }

            // Track which QS link each panel is assigned to (for special device counting)
            var panelLinkMap = new Dictionary<PanelResult, ProcessorLink>();

            // Auto-assign all non-processor panels to QS links only
            if (qsLinks.Count > 0)
            {
                int linkIndex = 0;
                foreach (var panel in allPanels)
                {
                    bool assigned = false;
                    for (int i = linkIndex; i < qsLinks.Count; i++)
                    {
                        var link = qsLinks[i];
                        int deviceRoom = ProcessorLink.MaxDevices - linkDevices[link];
                        int loadRoom = ProcessorLink.MaxLoads - linkLoads[link];

                        if (panel.DeviceCount <= deviceRoom && panel.LoadCount <= loadRoom)
                        {
                            linkDevices[link] += panel.DeviceCount;
                            linkLoads[link] += panel.LoadCount;
                            panelLinkMap[panel] = link;
                            assigned = true;
                            break;
                        }

                        // Current link is full for this panel — advance
                        linkIndex = i + 1;
                    }

                    // Fallback: if no QS link from linkIndex onward fits, use the one with most room
                    if (!assigned && qsLinks.Count > 0)
                    {
                        var bestLink = qsLinks
                            .OrderByDescending(l => Math.Min(
                                ProcessorLink.MaxDevices - linkDevices[l],
                                ProcessorLink.MaxLoads - linkLoads[l]))
                            .First();
                        linkDevices[bestLink] += panel.DeviceCount;
                        linkLoads[bestLink] += panel.LoadCount;
                        panelLinkMap[panel] = bestLink;
                    }
                }
            }

            // Count QSE-IO and QSE-CI-DMX as 1 device each on the link their panel is assigned to
            foreach (var panel in allPanels)
            {
                if (!panel.HasSpecialCompartment) continue;
                string selected = panel.SelectedSpecialDevice;
                if (string.Equals(selected, "Digital I/O", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(selected, "DMX", StringComparison.OrdinalIgnoreCase))
                {
                    if (panelLinkMap.TryGetValue(panel, out var link))
                        linkDevices[link] += 1;
                }
            }

            // Group keypads together on the QS link(s) with the most device headroom.
            // If all links are at/over capacity, assign remaining to the least-overloaded
            // link so the overflow is visible to the user.
            if (keypadCount > 0 && qsLinks.Count > 0)
            {
                int remaining = keypadCount;
                var linksByRoom = qsLinks
                    .OrderByDescending(l => ProcessorLink.MaxDevices - linkDevices[l])
                    .ToList();

                foreach (var link in linksByRoom)
                {
                    if (remaining <= 0) break;
                    int room = ProcessorLink.MaxDevices - linkDevices[link];
                    if (room <= 0) continue;
                    int assign = Math.Min(remaining, room);
                    linkDevices[link] += assign;
                    remaining -= assign;
                }

                // If keypads remain (all links full), pile onto the link with most headroom
                if (remaining > 0)
                {
                    var bestLink = linksByRoom[0];
                    linkDevices[bestLink] += remaining;
                }
            }

            // Update ProcessorLink properties (triggers INotifyPropertyChanged)
            foreach (var link in allLinks)
            {
                link.UsedDevices = linkDevices[link];
                link.UsedLoads = linkLoads[link];
            }
        }
    }
}
