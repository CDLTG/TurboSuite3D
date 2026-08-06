#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx
{
    /// <summary>
    /// A control-system PROFILE (the Link + Interface rungs 3–4 of the ladder on
    /// <see cref="DmxSolver"/>): the source of the
    /// version-/vendor-specific DEFAULTS the contract is pre-filled from — the DMX interface channel
    /// ceiling and the Link → Processor roll-up capacities. The engine never branches on the profile
    /// NAME; it only consumes the numbers a profile supplies, so adding a vendor is a data entry here,
    /// not an engine change.
    ///
    /// What a profile does NOT own: the Kind-2 job policy (breaker amps/volts/derate, inrush, D4,
    /// reserved channels) — those are job settings with their own defaults, profile-seeded then
    /// overridable (the window's Settings panel). And it does not own conventions (color → channels), which
    /// have no v1 UI (the even split is the silent default).
    /// </summary>
    public sealed class DmxProfile
    {
        public DmxProfile(string name, int channelCeiling, int linkChannelCapacity,
                          int linkDeviceCapacity, int linksPerProcessor)
        {
            Name = name;
            ChannelCeiling = channelCeiling;
            LinkChannelCapacity = linkChannelCapacity;
            LinkDeviceCapacity = linkDeviceCapacity;
            LinksPerProcessor = linksPerProcessor;
        }

        public string Name { get; }

        /// <summary>The DMX interface's channel budget atom ceiling (Lutron 32; native universe 512).</summary>
        public int ChannelCeiling { get; }

        /// <summary>Switch legs per control link (1 DMX channel = 1 leg). Lutron QS = 512.</summary>
        public int LinkChannelCapacity { get; }

        /// <summary>DMX interfaces per control link. Lutron QS = 16 (a per-device-type cap, NOT the
        /// link's 99-total-QS-device limit — see the Lutron constant below).</summary>
        public int LinkDeviceCapacity { get; }

        /// <summary>Links per processor for the roll-up (Lutron HQP7-2 = 2).</summary>
        public int LinksPerProcessor { get; }

        // ── Built-in profiles ──────────────────────────────────────────────────────────────────────
        // Lutron QSE-CI-DMX on a QS link off an HQP7-2 processor — the firm's house system, the default.
        // The device cap is 16, not the QS link's 99: 99 is the total device budget across all types,
        // while the QSE-CI-DMX submittal caps interfaces at "up to 16 per QS link in HomeWorks". The two
        // only coincide when every interface is packed full (16 × 32 = 512 = the leg cap); declared loops
        // routinely leave interfaces partly filled, and using 99 there under-reports links — and so
        // under-reports PROCESSORS, on a purchasing document.
        public static readonly DmxProfile Lutron = new DmxProfile("Lutron", 32, 512, 16, 2);

        // Crestron DIN-DMX — native universe ceiling; link/processor roll-up is a Lutron-ism, so the
        // capacities here are nominal report-only values (the roll-up is never provisioned).
        public static readonly DmxProfile Crestron = new DmxProfile("Crestron", 512, 512, 99, 2);

        // Generic ETC / Pathway gateway — native 512-channel universe, no vendor link semantics.
        public static readonly DmxProfile Generic = new DmxProfile("Generic", 512, 512, 99, 2);

        /// <summary>The built-in profiles, in display order (Lutron first — the house default).</summary>
        public static IReadOnlyList<DmxProfile> All { get; } = new[] { Lutron, Crestron, Generic };

        /// <summary>Look a profile up by name (case-insensitive); falls back to <see cref="Lutron"/>.</summary>
        public static DmxProfile ByName(string? name) =>
            All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Lutron;
    }
}
