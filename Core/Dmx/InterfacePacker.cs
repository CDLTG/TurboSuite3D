using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx
{
    /// <summary>One interface (DMX gateway) and the zones addressed within its universe.</summary>
    public sealed class DmxInterface
    {
        public DmxInterface(int interfaceNumber, IReadOnlyList<AddressedZone> zones, string? loopName = null)
        {
            InterfaceNumber = interfaceNumber;
            Zones = zones;
            LoopName = loopName;
        }

        /// <summary>1-based interface number (the "Interface #" on the one-line, §8a).</summary>
        public int InterfaceNumber { get; }
        public IReadOnlyList<AddressedZone> Zones { get; }

        /// <summary>
        /// The designer-declared DMX Loop name this interface realizes (§0d), or null if the engine
        /// auto-packed it. Brands the loop's one-line diagram identity.
        /// </summary>
        public string? LoopName { get; }

        public int ChannelsUsed => Zones.Sum(z => z.ChannelsConsumed);
    }

    /// <summary>The result of packing zones into interfaces, with the budget context that produced it.</summary>
    public sealed class InterfacePackResult
    {
        public InterfacePackResult(IReadOnlyList<DmxInterface> interfaces, int channelCeiling, int reservedChannels)
        {
            Interfaces = interfaces;
            ChannelCeiling = channelCeiling;
            ReservedChannels = reservedChannels;
        }

        public IReadOnlyList<DmxInterface> Interfaces { get; }

        /// <summary>Profile channel ceiling (§1.6): Lutron QSE-CI-DMX = 32, native universe = 512.</summary>
        public int ChannelCeiling { get; }

        /// <summary>Smart-fixture (Topology B) channels reserved off the budget before packing tape (§3c).</summary>
        public int ReservedChannels { get; }

        /// <summary>Channels available for tape zones per interface: ceiling − reserved.</summary>
        public int ChannelBudget => ChannelCeiling - ReservedChannels;

        public int InterfaceCount => Interfaces.Count;
    }

    /// <summary>
    /// Step 7 — pack zones into interfaces under the D1 budget (ceiling − reserved, §3c/§4), then
    /// address each interface's zones from slot 1 (each interface is its own universe). Zones are
    /// kept WHOLE within an interface (next-fit); a zone spanning interfaces — the expensive
    /// address-duplication case (§6c) — is the deferred physical-spread path, not done here.
    ///
    /// Designer-declared DMX Loops (§0d) override packing for their member zones: each declared loop
    /// becomes exactly one interface (in declaration order), branded with its loop name; the remaining
    /// (undeclared) zones auto-pack by next-fit after them. An over-budget declared loop is the third
    /// pre-solve gate (<see cref="DmxValidator"/>), so it should never reach here — this guards anyway.
    /// </summary>
    public static class InterfacePacker
    {
        /// <summary>Channels a zone consumes (= its declared channel count).</summary>
        public static int ChannelsOf(ZoneInput zone) => zone.Channels;

        public static InterfacePackResult Pack(IReadOnlyList<ZoneInput> zones, int channelCeiling,
            int reservedChannels = 0, IReadOnlyList<LoopDeclaration>? declaredLoops = null)
        {
            if (zones == null) throw new ArgumentNullException(nameof(zones));
            if (reservedChannels < 0) throw new ArgumentOutOfRangeException(nameof(reservedChannels));
            int budget = channelCeiling - reservedChannels;
            if (budget <= 0)
                throw new ArgumentException($"Reserved channels ({reservedChannels}) leave no budget under the ceiling ({channelCeiling}).");

            var byName = new Dictionary<string, ZoneInput>(StringComparer.OrdinalIgnoreCase);
            foreach (var z in zones) byName[z.ZoneName] = z;

            // Declared loops first, in declaration order — one interface each, branded with the loop name.
            var groups = new List<(string? loopName, List<ZoneInput> zones)>();
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (declaredLoops != null)
            {
                foreach (var loop in declaredLoops)
                {
                    var grp = new List<ZoneInput>(loop.ZoneNames.Count);
                    foreach (var zn in loop.ZoneNames)
                    {
                        if (!byName.TryGetValue(zn, out var zi))
                            throw new InvalidOperationException($"Declared loop '{loop.Name}' references unknown zone '{zn}'.");
                        grp.Add(zi);
                        claimed.Add(zn);
                    }
                    int sum = grp.Sum(ChannelsOf);
                    if (sum > budget)
                        throw new InvalidOperationException(
                            $"Declared loop '{loop.Name}' needs {sum} channels, more than one interface's budget ({budget}).");
                    groups.Add((loop.Name, grp));
                }
            }

            // Remaining (undeclared) zones auto-pack by next-fit: fill the current interface, spill when full.
            var current = new List<ZoneInput>();
            int used = 0;
            foreach (var zone in zones)
            {
                if (claimed.Contains(zone.ZoneName)) continue;
                int zc = ChannelsOf(zone);
                if (zc > budget)
                    throw new InvalidOperationException(
                        $"Zone '{zone.ZoneName}' needs {zc} channels, more than one interface's budget ({budget}).");

                if (used + zc > budget)
                {
                    groups.Add((null, current));
                    current = new List<ZoneInput>();
                    used = 0;
                }
                current.Add(zone);
                used += zc;
            }
            if (current.Count > 0) groups.Add((null, current));

            var interfaces = new List<DmxInterface>(groups.Count);
            for (int i = 0; i < groups.Count; i++)
                interfaces.Add(new DmxInterface(i + 1, Addresser.Assign(groups[i].zones), groups[i].loopName));

            return new InterfacePackResult(interfaces, channelCeiling, reservedChannels);
        }
    }
}
