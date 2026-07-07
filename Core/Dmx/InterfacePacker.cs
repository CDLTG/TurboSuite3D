using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx
{
    /// <summary>One interface (DMX gateway) and the zones addressed within its universe.</summary>
    public sealed class DmxInterface
    {
        public DmxInterface(int interfaceNumber, IReadOnlyList<AddressedZone> zones, string? loopName = null,
                            int reservedChannels = 0)
        {
            InterfaceNumber = interfaceNumber;
            Zones = zones;
            LoopName = loopName;
            ReservedChannels = reservedChannels;
        }

        /// <summary>1-based interface number (the "Interface #" on the one-line).</summary>
        public int InterfaceNumber { get; }
        public IReadOnlyList<AddressedZone> Zones { get; }

        /// <summary>
        /// The designer-declared DMX Loop name this interface realizes, or null if the engine
        /// auto-packed it. Brands the loop's one-line diagram identity.
        /// </summary>
        public string? LoopName { get; }

        /// <summary>Smart-fixture (Topology B) channels held off this interface's budget. Carried
        /// from the declared loop; auto-packed interfaces reserve nothing.</summary>
        public int ReservedChannels { get; }

        public int ChannelsUsed => Zones.Sum(z => z.ChannelsConsumed);
    }

    /// <summary>The result of packing zones into interfaces, with the ceiling context that produced it.</summary>
    public sealed class InterfacePackResult
    {
        public InterfacePackResult(IReadOnlyList<DmxInterface> interfaces, int channelCeiling)
        {
            Interfaces = interfaces;
            ChannelCeiling = channelCeiling;
        }

        public IReadOnlyList<DmxInterface> Interfaces { get; }

        /// <summary>Profile channel ceiling: Lutron QSE-CI-DMX = 32, native universe = 512.</summary>
        public int ChannelCeiling { get; }

        public int InterfaceCount => Interfaces.Count;
    }

    /// <summary>
    /// Step 7 — pack zones into interfaces under the D1 budget (ceiling − reserved), then
    /// address each interface's zones from slot 1 (each interface is its own universe). Zones are
    /// kept WHOLE within an interface (next-fit); a zone spanning interfaces — the expensive
    /// address-duplication case — is the deferred physical-spread path, not done here.
    ///
    /// Designer-declared DMX Loops override packing for their member zones: each declared loop
    /// becomes exactly one interface (in declaration order), branded with its loop name; the remaining
    /// (undeclared) zones auto-pack by next-fit after them. An over-budget declared loop is the third
    /// pre-solve gate (<see cref="DmxValidator"/>), so it should never reach here — this guards anyway.
    /// </summary>
    public static class InterfacePacker
    {
        /// <summary>Channels a zone consumes (= its declared channel count).</summary>
        public static int ChannelsOf(ZoneInput zone) => zone.Channels;

        public static InterfacePackResult Pack(IReadOnlyList<ZoneInput> zones, int channelCeiling,
            IReadOnlyList<LoopDeclaration>? declaredLoops = null)
        {
            if (zones == null) throw new ArgumentNullException(nameof(zones));

            var byName = new Dictionary<string, ZoneInput>(StringComparer.OrdinalIgnoreCase);
            foreach (var z in zones) byName[z.ZoneName] = z;

            // Declared loops first, in declaration order — one interface each, branded with the loop name.
            // Each carries its OWN reserved-channel headroom, so its budget is ceiling − its reserved.
            var groups = new List<(string? loopName, int reserved, List<ZoneInput> zones)>();
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (declaredLoops != null)
            {
                foreach (var loop in declaredLoops)
                {
                    int loopBudget = channelCeiling - loop.ReservedChannels;
                    if (loopBudget <= 0)
                        throw new InvalidOperationException(
                            $"Declared loop '{loop.Name}' reserves {loop.ReservedChannels} channels, leaving no budget under the ceiling ({channelCeiling}).");

                    var grp = new List<ZoneInput>(loop.ZoneNames.Count);
                    foreach (var zn in loop.ZoneNames)
                    {
                        if (!byName.TryGetValue(zn, out var zi))
                            throw new InvalidOperationException($"Declared loop '{loop.Name}' references unknown zone '{zn}'.");
                        grp.Add(zi);
                        claimed.Add(zn);
                    }
                    int sum = grp.Sum(ChannelsOf);
                    if (sum > loopBudget)
                        throw new InvalidOperationException(
                            $"Declared loop '{loop.Name}' needs {sum} channels, more than one interface's budget ({loopBudget}).");
                    groups.Add((loop.Name, loop.ReservedChannels, grp));
                }
            }

            // Remaining (undeclared) zones auto-pack by next-fit: fill the current interface, spill when full.
            // Auto-packed interfaces reserve nothing — the full ceiling is the budget.
            var current = new List<ZoneInput>();
            int used = 0;
            foreach (var zone in zones)
            {
                if (claimed.Contains(zone.ZoneName)) continue;
                int zc = ChannelsOf(zone);
                if (zc > channelCeiling)
                    throw new InvalidOperationException(
                        $"Zone '{zone.ZoneName}' needs {zc} channels, more than one interface's budget ({channelCeiling}).");

                if (used + zc > channelCeiling)
                {
                    groups.Add((null, 0, current));
                    current = new List<ZoneInput>();
                    used = 0;
                }
                current.Add(zone);
                used += zc;
            }
            if (current.Count > 0) groups.Add((null, 0, current));

            var interfaces = new List<DmxInterface>(groups.Count);
            for (int i = 0; i < groups.Count; i++)
                interfaces.Add(new DmxInterface(i + 1, Addresser.Assign(groups[i].zones), groups[i].loopName, groups[i].reserved));

            return new InterfacePackResult(interfaces, channelCeiling);
        }
    }
}
