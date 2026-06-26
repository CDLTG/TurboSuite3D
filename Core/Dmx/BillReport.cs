using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace TurboSuite.Dmx
{
    /// <summary>
    /// Renders a <see cref="DmxBill"/> as a human-readable list dump — Screenshot_196 in text form:
    /// totals, driver BOM, wire legend, then each interface's loop with its zones, addresses, and
    /// the decoders (DEC #, watts, driver) mirrored under each zone. Pure string output.
    /// </summary>
    public static class BillReport
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const string Rule = "============================================================";
        private const string Sub  = "------------------------------------------------------------";

        public static string Format(DmxBill bill, DmxContract contract)
        {
            var sb = new StringBuilder();
            var byZone = bill.Zones.ToDictionary(z => z.ZoneName);

            sb.AppendLine(Rule);
            sb.AppendLine(" TurboDMX — solve bill");
            sb.AppendLine(Rule);
            sb.AppendLine($" Contract: {Num(contract.SystemVolts)} V · ceiling {contract.ChannelCeiling} ch "
                          + $"(reserved {contract.ReservedChannels}) · D4 = {contract.MaxDevicesPerSegment} devices/segment");
            sb.AppendLine();

            sb.AppendLine(" TOTALS");
            sb.AppendLine($"   Decoders ........ {bill.TotalDecoders}");
            sb.AppendLine($"   Drivers ......... {bill.TotalDrivers}");
            sb.AppendLine($"   DMX channels .... {bill.TotalChannels}");
            sb.AppendLine($"   Interfaces ...... {bill.InterfaceCount}");
            sb.AppendLine($"   Links ........... {bill.RequiredLinks}   "
                          + $"(≤ {contract.LinkChannelCapacity} legs / ≤ {contract.LinkDeviceCapacity} devices each — reported, not provisioned)");
            sb.AppendLine($"   Processors ...... {bill.RequiredProcessors}   (≤ {contract.LinksPerProcessor} links each)");
            sb.AppendLine($"   Repeaters ....... {bill.TotalRepeaters}");
            sb.AppendLine($"   Connected load .. {bill.TotalWatts.ToString("N0", Inv)} W");
            string basis = contract.BreakerBasis == BreakerBasis.DriverRating ? "by driver rating" : "by connected load";
            sb.AppendLine($"   120V feeds ...... {bill.RequiredBreakers}   "
                          + $"({Num(contract.BreakerAmps)} A @ {Num(contract.FeedVolts)} V, cap {contract.BreakerCapWatts.ToString("N0", Inv)} W, {basis}"
                          + $"{(contract.MaxDriversPerBreaker > 0 ? $", ≤ {contract.MaxDriversPerBreaker} drivers/breaker" : "")})");
            sb.AppendLine();

            sb.AppendLine(" DECODERS (BOM)");
            foreach (var kv in bill.DecodersByType.OrderByDescending(k => k.Value))
                sb.AppendLine($"   {kv.Key} × {kv.Value}");
            sb.AppendLine();

            sb.AppendLine(" DRIVERS (BOM)");
            foreach (var kv in bill.DriversByType.OrderByDescending(k => k.Value))
                sb.AppendLine($"   {kv.Key} × {kv.Value}");
            sb.AppendLine();

            sb.AppendLine(" WIRE LEGEND");
            foreach (var channels in bill.Zones.Select(z => z.Channels).Distinct().OrderBy(c => c))
                sb.AppendLine($"   decoder → tape ({channels} ch) : {WireSpec.TapeCable(channels)}");
            sb.AppendLine($"   breaker → driver (HV)  : {WireSpec.DriverFeedCable}");
            sb.AppendLine();

            int dec = 0;
            foreach (var iface in bill.Interfaces)
            {
                sb.AppendLine(Sub);
                string loopTag = iface.Interface.LoopName != null
                    ? $"  — loop \"{iface.Interface.LoopName}\" (declared)"
                    : "  — auto-packed";
                sb.AppendLine($" INTERFACE #{iface.Interface.InterfaceNumber}   ({iface.Interface.ChannelsUsed} / {contract.ChannelCeiling} channels){loopTag}");
                sb.AppendLine($"   Loop: {iface.DeviceCount} devices · {Segments(iface.Segmentation)}");

                foreach (var zone in iface.Interface.Zones)
                {
                    var sol = byZone[zone.ZoneName];
                    string overClusters = sol.Clusters.Count > 1 ? $" over {sol.Clusters.Count} clusters" : "";
                    sb.AppendLine($"   Zone \"{zone.ZoneName}\" [{sol.Channels} ch · {sol.Decoder.Name}]   {Addresses(zone)}");
                    sb.AppendLine($"     mirrored across {sol.DecoderCount} decoder(s){overClusters}:");
                    foreach (var cluster in sol.Clusters)
                    {
                        if (sol.Clusters.Count > 1)
                            sb.AppendLine($"     ├ {cluster.Name} ({cluster.DecoderCount} dec):");
                        foreach (var pd in cluster.Power.Decoders)
                            sb.AppendLine($"       DEC {++dec,-3} {Num(pd.Decoder.TotalWatts),5} W   ← driver {pd.Driver.Name} ({Num(pd.Driver.RatedWatts)} W)");
                    }
                }
            }
            sb.AppendLine(Rule);
            return sb.ToString();
        }

        private static string Addresses(AddressedZone zone)
        {
            var parts = zone.SubZones.Select(s =>
            {
                int end = s.StartAddress + s.ChannelCount - 1;
                string span = s.ChannelCount == 1 ? Addr(s.StartAddress) : $"{Addr(s.StartAddress)}-{Addr(end)}";
                return $"{s.Role.ToString().ToLowerInvariant()} @{span}";
            });
            return "addresses: " + string.Join(", ", parts);
        }

        private static string Segments(LoopSegmentation seg)
        {
            if (seg.SegmentCount <= 1) return "1 segment · 0 repeaters";
            string sizes = string.Join("/", seg.Segments.Select(s => s.DeviceCount));
            return $"{seg.SegmentCount} segments ({sizes}) · {seg.RepeaterCount} repeater(s)";
        }

        private static string Addr(int a) => a.ToString("000", Inv);
        private static string Num(double d) => d.ToString("0.##", Inv);
    }
}
