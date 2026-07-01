using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TurboSuite.Dmx
{
    /// <summary>A parsed scenario file: the contract plus the tagged zones, ready for <see cref="DmxSolver"/>.</summary>
    public sealed class Scenario
    {
        public Scenario(DmxContract contract, IReadOnlyList<ZoneDesign> zones,
                        IReadOnlyList<LoopDeclaration>? loops = null)
        {
            Contract = contract;
            Zones = zones;
            Loops = loops ?? new List<LoopDeclaration>();
        }

        public DmxContract Contract { get; }
        public IReadOnlyList<ZoneDesign> Zones { get; }

        /// <summary>Designer-declared DMX Loops (§0d), empty if none — then zones pure-auto-pack.</summary>
        public IReadOnlyList<LoopDeclaration> Loops { get; }
    }

    /// <summary>
    /// Parser for the plain line-based scenario file (see Harness/sample.dmx.txt). Format:
    /// `key = value` scalars, repeatable `decoder =` / `driver =` / `zone =` lines, `#` comments.
    /// Deliberately forgiving and simple — it's a dev I/O harness, not a shipping format.
    /// </summary>
    public static class ScenarioParser
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static Scenario Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            double volts = 24, wattsPerFt = 5.2;
            int ceiling = 32, d4 = 32;
            double breakerAmps = 20, feedVolts = 120, breakerDerate = 0.8;
            int maxPerBreaker = 0;
            var breakerBasis = BreakerBasis.ConnectedLoad;
            int linkChannels = 512, linkDevices = 99, linksPerProcessor = 2;
            var decoders = new List<DecoderSpec>();
            var drivers = new List<DriverType>();
            var zoneOrder = new List<string>();
            var zoneChannels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var zoneClusters = new Dictionary<string, List<RunCluster>>(StringComparer.OrdinalIgnoreCase);
            var loops = new List<LoopDeclaration>();

            int lineNo = 0;
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                lineNo++;
                string line = raw;
                int hash = line.IndexOf('#'); // strip full-line and inline comments (no input value contains '#')
                if (hash >= 0) line = line.Substring(0, hash);
                line = line.Trim();
                if (line.Length == 0) continue;

                int eq = line.IndexOf('=');
                if (eq < 0) throw new FormatException($"Line {lineNo}: expected 'key = value' — '{line}'");
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();

                switch (key)
                {
                    case "volts": volts = D(val, lineNo); break;
                    case "ceiling": ceiling = I(val, lineNo); break;
                    case "d4": d4 = I(val, lineNo); break;
                    case "wattsperft": wattsPerFt = D(val, lineNo); break;
                    case "breakeramps": breakerAmps = D(val, lineNo); break;
                    case "feedvolts": feedVolts = D(val, lineNo); break;
                    case "breakerderate": breakerDerate = D(val, lineNo); break;
                    case "maxperbreaker": maxPerBreaker = I(val, lineNo); break;
                    case "breakerbasis": breakerBasis = ParseBasis(val, lineNo); break;
                    case "linkchannels": linkChannels = I(val, lineNo); break;
                    case "linkdevices": linkDevices = I(val, lineNo); break;
                    case "linksperprocessor": linksPerProcessor = I(val, lineNo); break;
                    case "decoder": decoders.Add(ParseDecoder(val, lineNo)); break;
                    case "driver": drivers.Add(ParseDriver(val, lineNo)); break;
                    case "zone": ParseZoneLine(val, wattsPerFt, lineNo, zoneOrder, zoneChannels, zoneClusters); break;
                    case "cluster": ParseClusterLine(val, wattsPerFt, lineNo, zoneChannels, zoneClusters); break;
                    case "loop": loops.Add(ParseLoopLine(val, lineNo)); break;
                    default: throw new FormatException($"Line {lineNo}: unknown key '{key}'");
                }
            }

            if (decoders.Count == 0) throw new FormatException("No 'decoder =' lines — the decoder pool is empty.");
            if (drivers.Count == 0) throw new FormatException("No 'driver =' lines — the driver pool is empty.");
            if (zoneOrder.Count == 0) throw new FormatException("No 'zone =' lines — nothing to solve.");

            var zones = new List<ZoneDesign>(zoneOrder.Count);
            foreach (var name in zoneOrder)
            {
                var clusters = zoneClusters[name];
                if (clusters.Count == 0)
                    throw new FormatException($"Zone '{name}' has no runs — add lengths on the zone line or a 'cluster =' line.");
                zones.Add(new ZoneDesign(name, clusters));
            }

            var contract = new DmxContract(decoders, drivers, volts, ceiling, d4,
                                           breakerAmps, feedVolts, breakerDerate, maxPerBreaker, breakerBasis,
                                           linkChannels, linkDevices, linksPerProcessor);
            return new Scenario(contract, zones, loops);
        }

        // loop = <loopName> | <zoneA>, <zoneB>, ...  [ | <reservedChannels> ]   (a declared DMX Loop, §0d)
        private static LoopDeclaration ParseLoopLine(string val, int lineNo)
        {
            var parts = val.Split('|');
            if (parts.Length != 2 && parts.Length != 3)
                throw new FormatException($"Line {lineNo}: loop needs 'name | zoneA, zoneB, ...' (optionally '| reservedChannels')");
            string name = parts[0].Trim();
            if (name.Length == 0) throw new FormatException($"Line {lineNo}: loop needs a name");
            var zoneNames = parts[1].Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (zoneNames.Count == 0) throw new FormatException($"Line {lineNo}: loop '{name}' lists no zones");
            int reserved = 0;
            if (parts.Length == 3 && parts[2].Trim().Length > 0) reserved = I(parts[2].Trim(), lineNo);
            return new LoopDeclaration(name, zoneNames, reserved);
        }

        // decoder = <name...> outputs:N amps:A watts:W   (name = the tokens without a colon)
        private static DecoderSpec ParseDecoder(string val, int lineNo)
        {
            int outputs = 0; double amps = 0, watts = 0;
            var nameTokens = new List<string>();
            foreach (var tok in val.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = tok.IndexOf(':');
                if (colon < 0) { nameTokens.Add(tok); continue; }
                string field = tok.Substring(0, colon).ToLowerInvariant();
                string fv = tok.Substring(colon + 1);
                switch (field)
                {
                    case "outputs": outputs = I(fv, lineNo); break;
                    case "amps": amps = D(fv, lineNo); break;
                    case "watts": watts = D(fv, lineNo); break;
                    default: throw new FormatException($"Line {lineNo}: unknown decoder field '{field}'");
                }
            }
            if (outputs <= 0 || amps <= 0 || watts <= 0)
                throw new FormatException($"Line {lineNo}: decoder needs outputs:, amps: and watts:");
            string name = nameTokens.Count > 0 ? string.Join(" ", nameTokens) : $"{outputs}ch";
            return new DecoderSpec(name, outputs, amps, watts);
        }

        // driver = <name...> <ratedW> <volts> <derate>   (name may contain spaces)
        private static DriverType ParseDriver(string val, int lineNo)
        {
            var t = val.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 4) throw new FormatException($"Line {lineNo}: driver needs '<name> <ratedW> <volts> <derate>'");
            double derate = D(t[t.Length - 1], lineNo);
            double dv = D(t[t.Length - 2], lineNo);
            double rated = D(t[t.Length - 3], lineNo);
            string name = string.Join(" ", t.Take(t.Length - 3));
            return new DriverType(name, rated, dv, derate);
        }

        // zone = <name> | <channels>            (clusters added by 'cluster =' lines)
        // zone = <name> | <channels> | <lens>   (single default cluster — the pre-cluster form)
        private static void ParseZoneLine(string val, double wattsPerFt, int lineNo, List<string> order,
                                          Dictionary<string, int> channelsByZone, Dictionary<string, List<RunCluster>> clustersByZone)
        {
            var parts = val.Split('|');
            if (parts.Length != 2 && parts.Length != 3)
                throw new FormatException($"Line {lineNo}: zone needs 'name | channels' or 'name | channels | lengths'");
            string name = parts[0].Trim();
            int channels = I(parts[1].Trim(), lineNo);
            if (channels <= 0) throw new FormatException($"Line {lineNo}: zone '{name}' channels must be ≥ 1");

            if (!channelsByZone.ContainsKey(name)) { order.Add(name); clustersByZone[name] = new List<RunCluster>(); }
            channelsByZone[name] = channels;

            if (parts.Length == 3) // inline runs ⇒ one default cluster named after the zone
            {
                var runs = ParseRuns(parts[2], channels, wattsPerFt, lineNo);
                if (runs.Length == 0) throw new FormatException($"Line {lineNo}: zone '{name}' has no run lengths");
                clustersByZone[name].Add(new RunCluster(name, runs));
            }
        }

        // cluster = <zoneName> | <clusterName> | <len, len, ...>   (a physical group within a zone, §8d)
        private static void ParseClusterLine(string val, double wattsPerFt, int lineNo,
                                             Dictionary<string, int> channelsByZone, Dictionary<string, List<RunCluster>> clustersByZone)
        {
            var parts = val.Split('|');
            if (parts.Length != 3) throw new FormatException($"Line {lineNo}: cluster needs 'zone | clusterName | lengths'");
            string zoneName = parts[0].Trim();
            string clusterName = parts[1].Trim();
            if (!channelsByZone.TryGetValue(zoneName, out int channels))
                throw new FormatException($"Line {lineNo}: cluster references zone '{zoneName}' before it's declared");
            var runs = ParseRuns(parts[2], channels, wattsPerFt, lineNo);
            if (runs.Length == 0) throw new FormatException($"Line {lineNo}: cluster '{clusterName}' has no run lengths");
            clustersByZone[zoneName].Add(new RunCluster(clusterName, runs));
        }

        private static TapeRun[] ParseRuns(string list, int channels, double wattsPerFt, int lineNo) =>
            list.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)
                .SelectMany(s => ExpandRun(s, wattsPerFt, channels, lineNo))
                .ToArray();

        // One run token, optionally with a repeat multiplier: "17.2", "17.2 ×72", "17.2 x72", "17.2*72".
        private static IEnumerable<TapeRun> ExpandRun(string token, double wattsPerFt, int channels, int lineNo)
        {
            int count = 1;
            string lenText = token;
            int m = token.IndexOfAny(new[] { '×', 'x', 'X', '*' }); // lengths never contain these
            if (m >= 0)
            {
                lenText = token.Substring(0, m).Trim();
                count = I(token.Substring(m + 1).Trim(), lineNo);
                if (count <= 0) throw new FormatException($"Line {lineNo}: run multiplier must be ≥ 1 — '{token}'");
            }
            double feet = ParseFeet(lenText);
            for (int i = 0; i < count; i++) yield return new TapeRun(feet, wattsPerFt, channels);
        }

        // breakerBasis = load | rating  (rating/nameplate = pack by full driver nameplate)
        private static BreakerBasis ParseBasis(string val, int lineNo)
        {
            switch (val.Trim().ToLowerInvariant())
            {
                case "load": case "connectedload": case "connected": return BreakerBasis.ConnectedLoad;
                case "rating": case "rated": case "nameplate": return BreakerBasis.DriverRating;
                default: throw new FormatException($"Line {lineNo}: breakerBasis must be 'load' or 'rating' — '{val}'");
            }
        }

        /// <summary>Parse a length as feet: "66'9", "66'-9\"", "42'0" or decimal "23.27".</summary>
        public static double ParseFeet(string s)
        {
            s = s.Trim();
            int tick = s.IndexOf('\'');
            if (tick < 0) return double.Parse(s, Inv);
            double feet = double.Parse(s.Substring(0, tick), Inv);
            string inchPart = s.Substring(tick + 1).Replace("-", "").Replace("\"", "").Trim();
            double inches = inchPart.Length == 0 ? 0 : double.Parse(inchPart, Inv);
            return feet + inches / 12.0;
        }

        private static double D(string s, int lineNo)
        {
            if (double.TryParse(s, NumberStyles.Any, Inv, out var d)) return d;
            throw new FormatException($"Line {lineNo}: '{s}' is not a number");
        }

        private static int I(string s, int lineNo)
        {
            if (int.TryParse(s, NumberStyles.Any, Inv, out var i)) return i;
            throw new FormatException($"Line {lineNo}: '{s}' is not an integer");
        }
    }
}
