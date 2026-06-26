using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx
{
    /// <summary>The addressable color dimension a sub-zone represents (for naming the Lutron zones).</summary>
    public enum ColorRole { Rgb, White, Cool, Warm, Amber }

    /// <summary>One sub-zone primitive: a color role and how many DMX channels it spans.</summary>
    public readonly struct SubZoneSpec
    {
        public SubZoneSpec(ColorRole role, int channelCount) { Role = role; ChannelCount = channelCount; }
        public ColorRole Role { get; }
        public int ChannelCount { get; }
    }

    /// <summary>
    /// Option (a): the count → sub-zone structure convention. A fixture declares only its CHANNEL
    /// COUNT (read from the family); this table decides how those channels decompose into named,
    /// separately-addressable Lutron primitives (1-ch singles + 3-ch rgb objects, §3a). It affects
    /// ONLY addressing/naming — never watts, decoder selection, or budget, which use the count alone.
    /// These are the defaults; the settings layer can override a row (e.g. an unusual 6-ch tape).
    /// </summary>
    public static class SubZoneStructure
    {
        /// <summary>The default decomposition for a channel count. Sizes always sum to the count.</summary>
        public static IReadOnlyList<SubZoneSpec> For(int channels)
        {
            switch (channels)
            {
                case 1: return new[] { S(ColorRole.White, 1) };
                case 2: return new[] { S(ColorRole.Cool, 1), S(ColorRole.Warm, 1) };
                case 3: return new[] { S(ColorRole.Rgb, 3) };
                case 4: return new[] { S(ColorRole.Rgb, 3), S(ColorRole.White, 1) };
                case 5: return new[] { S(ColorRole.Rgb, 3), S(ColorRole.Cool, 1), S(ColorRole.Warm, 1) };
                case 6: return new[] { S(ColorRole.Rgb, 3), S(ColorRole.Amber, 1), S(ColorRole.Cool, 1), S(ColorRole.Warm, 1) };
                default:
                    throw new ArgumentOutOfRangeException(nameof(channels), channels,
                        "No default sub-zone structure for this channel count — add one to the structure table.");
            }
        }

        private static SubZoneSpec S(ColorRole role, int size) => new SubZoneSpec(role, size);
    }

    /// <summary>One control zone the designer tagged: a name, its channel count, and the decoders it groups.</summary>
    public sealed class ZoneInput
    {
        public ZoneInput(string zoneName, int channels, int decoderCount)
        {
            ZoneName = zoneName;
            Channels = channels;
            DecoderCount = decoderCount;
        }

        public string ZoneName { get; }
        public int Channels { get; }

        /// <summary>How many decoders carry this zone — they all MIRROR the same address(es).</summary>
        public int DecoderCount { get; }
    }

    /// <summary>A sub-zone after addressing: its start slot in the 512-channel universe and its span.</summary>
    public sealed class AddressedSubZone
    {
        public AddressedSubZone(string zoneName, ColorRole role, int startAddress, int channelCount)
        {
            ZoneName = zoneName;
            Role = role;
            StartAddress = startAddress;
            ChannelCount = channelCount;
        }

        public string ZoneName { get; }
        public ColorRole Role { get; }

        /// <summary>The DIP-switch start address. Mirrored onto every decoder in the zone.</summary>
        public int StartAddress { get; }
        public int ChannelCount { get; }
    }

    /// <summary>A zone after addressing: its sub-zone address blocks, mirrored across its decoders.</summary>
    public sealed class AddressedZone
    {
        public AddressedZone(string zoneName, IReadOnlyList<AddressedSubZone> subZones, int decoderCount)
        {
            ZoneName = zoneName;
            SubZones = subZones;
            DecoderCount = decoderCount;
        }

        public string ZoneName { get; }
        public IReadOnlyList<AddressedSubZone> SubZones { get; }
        public int DecoderCount { get; }

        /// <summary>Channels this zone consumes — independent of decoder count (mirroring is free, §6).</summary>
        public int ChannelsConsumed => SubZones.Sum(s => s.ChannelCount);
    }

    /// <summary>
    /// Addressing. Explodes each zone into its sub-zones (per <see cref="SubZoneStructure"/>) and walks
    /// a single cursor across the universe assigning each a contiguous block (stride = its channel count).
    /// The zone's decoders all mirror these addresses, so adding decoders costs zero channels (§6).
    /// </summary>
    public static class Addresser
    {
        public static IReadOnlyList<AddressedZone> Assign(IReadOnlyList<ZoneInput> zones, int startAddress = 1)
        {
            if (zones == null) throw new ArgumentNullException(nameof(zones));

            int cursor = startAddress;
            var result = new List<AddressedZone>(zones.Count);
            foreach (var zone in zones)
            {
                var addressed = new List<AddressedSubZone>();
                foreach (var spec in SubZoneStructure.For(zone.Channels))
                {
                    addressed.Add(new AddressedSubZone(zone.ZoneName, spec.Role, cursor, spec.ChannelCount));
                    cursor += spec.ChannelCount;
                }
                result.Add(new AddressedZone(zone.ZoneName, addressed, zone.DecoderCount));
            }
            return result;
        }
    }
}
