using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx
{
    /// <summary>
    /// One control Link (e.g. a Lutron QS wired link) and the DMX interfaces hung on it. A link's DMX
    /// usage is sized under two co-equal caps (the Link, rung 3 of the ladder on
    /// <see cref="DmxSolver"/>): **switch legs** (1 DMX channel =
    /// 1 leg) and **link devices** (here the interfaces — the QS-link "devices", distinct from the
    /// decoders, which are DMX-Loop devices). Report-only: the engine sizes &amp; reports DMX
    /// demand; provisioning links/processors stays in the control system.
    /// </summary>
    public sealed class LinkLoad
    {
        private readonly List<int> _interfaceChannels = new List<int>();

        internal void Add(int interfaceChannels) => _interfaceChannels.Add(interfaceChannels);

        /// <summary>The channel (switch-leg) count of each interface on this link.</summary>
        public IReadOnlyList<int> InterfaceChannels => _interfaceChannels;

        /// <summary>Interfaces on the link = the QS-link devices TurboDMX contributes.</summary>
        public int InterfaceCount => _interfaceChannels.Count;

        /// <summary>Switch legs used = Σ interface channels.</summary>
        public int ChannelsUsed => _interfaceChannels.Sum();

        public int RemainingChannels(int cap) => cap - ChannelsUsed;
    }

    /// <summary>
    /// The Link/Processor roll-up — a pure REPORT pass, not a solve stop (D2 is
    /// report-only). Packs the solved interfaces onto control Links under two co-equal caps —
    /// switch legs (channels) AND device count (interfaces per link) — then rolls Links up to Processors
    /// by a fixed links-per-processor capacity (e.g. HQP7-2 = 2). First-Fit-Decreasing by channels, like
    /// <see cref="BreakerPacker"/>. The engine emits the COUNTS; provisioning stays in the control system.
    /// </summary>
    public static class LinkPacker
    {
        /// <summary>
        /// Pack interface channel counts onto links under the leg cap AND the device (interface) count cap
        /// (each ≤ 0 ⇒ that cap is unlimited). An interface larger than the leg cap throws (misconfigured).
        /// </summary>
        public static IReadOnlyList<LinkLoad> Pack(IReadOnlyList<int> interfaceChannels, int channelCap, int deviceCap)
        {
            if (interfaceChannels == null) throw new ArgumentNullException(nameof(interfaceChannels));

            var links = new List<LinkLoad>();
            foreach (int ch in interfaceChannels.OrderByDescending(x => x))
            {
                if (channelCap > 0 && ch > channelCap)
                    throw new InvalidOperationException(
                        $"An interface uses {ch} channels, more than one link's switch-leg cap ({channelCap}).");

                LinkLoad? target = null;
                foreach (var link in links)
                {
                    bool legsFit = channelCap <= 0 || ch <= link.RemainingChannels(channelCap);
                    bool devicesFit = deviceCap <= 0 || link.InterfaceCount < deviceCap;
                    if (legsFit && devicesFit) { target = link; break; }
                }
                if (target == null) { target = new LinkLoad(); links.Add(target); }
                target.Add(ch);
            }
            return links;
        }

        /// <summary>Processors needed to host the links: ceil(links / links-per-processor) (≥1 capacity).</summary>
        public static int ProcessorCount(int linkCount, int linksPerProcessor)
        {
            int cap = Math.Max(1, linksPerProcessor);
            return (int)Math.Ceiling((double)linkCount / cap);
        }
    }
}
