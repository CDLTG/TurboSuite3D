using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx
{
    /// <summary>
    /// One run the designer drew too long to be fed by a single decoder/driver — a DRAWING error under
    /// the drawn-correctly contract (Design §0b). Carries everything a "redraw this" flag needs: which
    /// zone/run, its watts vs. the buildable feed cap, and the minimum split (pieces + max length each).
    /// </summary>
    public readonly struct OverCapRun
    {
        public OverCapRun(string zoneName, int runIndex, double watts, double lengthFt,
                          double capWatts, int minPieces, double maxLengthFt)
        {
            ZoneName = zoneName;
            RunIndex = runIndex;
            Watts = watts;
            LengthFt = lengthFt;
            CapWatts = capWatts;
            MinPieces = minPieces;
            MaxLengthFt = maxLengthFt;
        }

        public string ZoneName { get; }
        public int RunIndex { get; }
        public double Watts { get; }
        public double LengthFt { get; }

        /// <summary>The coupled effective feed cap (derate included) the run had to fit and didn't.</summary>
        public double CapWatts { get; }

        /// <summary>ceil(watts / cap) — the fewest pieces the designer must split the run into.</summary>
        public int MinPieces { get; }

        /// <summary>cap / wattsPerFt — the longest a single piece may be drawn.</summary>
        public double MaxLengthFt { get; }

        /// <summary>One-line, located, actionable flag text for a dialog/report.</summary>
        public string Describe() =>
            $"Zone '{ZoneName}' run #{RunIndex + 1}: {LengthFt:F1} ft = {Watts:F0} W exceeds the "
            + $"{CapWatts:F0} W feed cap. Split into ≥ {MinPieces} runs (≤ {MaxLengthFt:F1} ft / ≤ {CapWatts:F0} W each).";
    }

    /// <summary>
    /// Raised when one or more runs are drawn too long for any single feed (drawn-correctly contract,
    /// Design §0b). Unlike <see cref="UnmappableTapeException"/> (a one-shot CONTRACT error), this is a
    /// DRAWING error: it BATCHES every offender so the designer fixes them all in one editing pass. The
    /// whole solve is refused — no partial bill — until the drawing is clean.
    /// </summary>
    public sealed class OverCapRunsException : Exception
    {
        public OverCapRunsException(IReadOnlyList<OverCapRun> violations)
            : base(BuildMessage(violations))
        {
            Violations = violations;
        }

        public IReadOnlyList<OverCapRun> Violations { get; }

        private static string BuildMessage(IReadOnlyList<OverCapRun> violations)
        {
            if (violations == null || violations.Count == 0)
                return "One or more runs exceed the feed cap.";
            var lines = violations.Select(v => "  • " + v.Describe());
            return $"{violations.Count} run(s) drawn too long for a single feed — split them and re-run:\n"
                   + string.Join("\n", lines);
        }
    }

    /// <summary>
    /// A designer-declared DMX Loop (§0d) assigned more channels than one interface/chain can carry —
    /// a DECLARATION error: the loop is a physical chain capped at the interface ceiling, and the cable
    /// break is a geometry call the engine won't make for the designer. Carries everything a "re-declare
    /// this loop" flag needs: the loop, its channel sum vs. the budget, and the minimum loop count.
    /// </summary>
    public readonly struct OverCapLoop
    {
        public OverCapLoop(string loopName, int channels, int budget, int minLoops)
        {
            LoopName = loopName;
            Channels = channels;
            Budget = budget;
            MinLoops = minLoops;
        }

        public string LoopName { get; }

        /// <summary>Sum of the member zones' channel counts.</summary>
        public int Channels { get; }

        /// <summary>The interface budget the loop had to fit and didn't (ceiling − reserved).</summary>
        public int Budget { get; }

        /// <summary>ceil(channels / budget) — the fewest loops the designer must break this into.</summary>
        public int MinLoops { get; }

        /// <summary>One-line, located, actionable flag text for a dialog/report.</summary>
        public string Describe() =>
            $"Loop '{LoopName}': {Channels} channels exceed the {Budget}-channel interface ceiling. "
            + $"Split into ≥ {MinLoops} loops (≤ {Budget} channels each).";
    }

    /// <summary>
    /// Raised when one or more DECLARED DMX Loops exceed the interface ceiling (Design §0d) — the third
    /// pre-solve gate. Like <see cref="OverCapRunsException"/> it BATCHES every offender so the designer
    /// re-declares them all in one pass, and refuses the whole solve (no partial bill). Distinct from
    /// capacity overflow, which the engine still silently auto-fills for UNDECLARED zones.
    /// </summary>
    public sealed class OverCapLoopsException : Exception
    {
        public OverCapLoopsException(IReadOnlyList<OverCapLoop> violations)
            : base(BuildMessage(violations))
        {
            Violations = violations;
        }

        public IReadOnlyList<OverCapLoop> Violations { get; }

        private static string BuildMessage(IReadOnlyList<OverCapLoop> violations)
        {
            if (violations == null || violations.Count == 0)
                return "One or more declared loops exceed the interface ceiling.";
            var lines = violations.Select(v => "  • " + v.Describe());
            return $"{violations.Count} declared loop(s) exceed the interface ceiling — re-declare them and re-run:\n"
                   + string.Join("\n", lines);
        }
    }

    /// <summary>
    /// A malformed loop declaration (§0d) — a loop referencing a zone that doesn't exist, or a zone
    /// placed in more than one loop (a chain can't carry the same zone twice; spanning loops is the §6
    /// address-duplication path, out of scope). An input bug, not the capacity gate, so it throws
    /// immediately rather than batching.
    /// </summary>
    public sealed class LoopDeclarationException : Exception
    {
        public LoopDeclarationException(string message) : base(message) { }
    }

    /// <summary>
    /// The pre-solve gate enforcing the drawn-correctly contract (Design §0b): the engine NEVER silently
    /// cuts a drawn run to fit. It first confirms every zone maps to a decoder (else the §6c contract
    /// abort), then flags every run whose watts exceed its zone's coupled feed cap so the designer
    /// redraws it. The cap is the buildable feed — min(decoder C1/C2, largest driver × derate) — so the
    /// derate INPUT moves the threshold (tighter derate ⇒ shorter max run ⇒ more pieces). Finally it
    /// enforces declared DMX Loops against the interface ceiling (§0d, the third gate).
    /// </summary>
    public static class DmxValidator
    {
        private const double Eps = 1e-9;

        /// <summary>
        /// Throw if the contract+zones can't be built as drawn. Mappability first, then over-cap runs
        /// (batched), then declared-loop integrity + over-ceiling (batched).
        /// </summary>
        public static void Validate(DmxContract contract, IReadOnlyList<ZoneDesign> zones,
                                    IReadOnlyList<LoopDeclaration>? loops = null)
        {
            if (contract == null) throw new ArgumentNullException(nameof(contract));
            if (zones == null) throw new ArgumentNullException(nameof(zones));

            // Pass 1 — decoder mappability: a CONTRACT error, run-breaking, abort on the first (§6c).
            foreach (var zone in zones)
            {
                if (zone.Runs.Count == 0) continue;
                int channels = DecoderPacker.SingleChannelsOf(zone.Runs);
                if (DecoderSelector.SelectForChannels(contract.DecoderPool, channels) is null)
                    throw new UnmappableTapeException(zone.ZoneName, channels,
                        DecoderSelector.MaxOutputs(contract.DecoderPool));
            }

            // Pass 2 — over-cap runs: a DRAWING error, batched across all zones.
            var violations = FindOverCapRuns(contract, zones);
            if (violations.Count > 0)
                throw new OverCapRunsException(violations);

            // Pass 3 — declared loops (§0d): integrity (throws), then over-ceiling (batched gate).
            if (loops != null && loops.Count > 0)
            {
                CheckLoopIntegrity(zones, loops);
                var loopViolations = FindOverCapLoops(contract, zones, loops);
                if (loopViolations.Count > 0)
                    throw new OverCapLoopsException(loopViolations);
            }
        }

        /// <summary>
        /// Validate that every declared loop references an existing zone and that no zone is in two loops.
        /// Throws <see cref="LoopDeclarationException"/> on the first malformed declaration.
        /// </summary>
        public static void CheckLoopIntegrity(IReadOnlyList<ZoneDesign> zones, IReadOnlyList<LoopDeclaration> loops)
        {
            if (zones == null) throw new ArgumentNullException(nameof(zones));
            if (loops == null) return;

            var known = new HashSet<string>(zones.Select(z => z.ZoneName), StringComparer.OrdinalIgnoreCase);
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // zone → owning loop
            foreach (var loop in loops)
            {
                foreach (var zn in loop.ZoneNames)
                {
                    if (!known.Contains(zn))
                        throw new LoopDeclarationException(
                            $"Declared loop '{loop.Name}' references zone '{zn}', which isn't a declared zone.");
                    if (seen.TryGetValue(zn, out var owner))
                        throw new LoopDeclarationException(
                            $"Zone '{zn}' is in two loops ('{owner}' and '{loop.Name}') — a chain can't carry it twice.");
                    seen[zn] = loop.Name;
                }
            }
        }

        /// <summary>
        /// Every declared loop whose member zones sum to more channels than the interface budget
        /// (ceiling − reserved), with the minimum loop count — for a UI "re-declare these" list. Empty ⇒
        /// every declared loop fits one chain. Assumes integrity (run <see cref="CheckLoopIntegrity"/> first).
        /// </summary>
        public static IReadOnlyList<OverCapLoop> FindOverCapLoops(DmxContract contract,
            IReadOnlyList<ZoneDesign> zones, IReadOnlyList<LoopDeclaration> loops)
        {
            if (contract == null) throw new ArgumentNullException(nameof(contract));
            if (zones == null) throw new ArgumentNullException(nameof(zones));
            if (loops == null) return new List<OverCapLoop>();

            int budget = contract.ChannelCeiling - contract.ReservedChannels;
            var channelsByZone = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var zone in zones)
                channelsByZone[zone.ZoneName] = zone.Runs.Count == 0 ? 0 : DecoderPacker.SingleChannelsOf(zone.Runs);

            var violations = new List<OverCapLoop>();
            foreach (var loop in loops)
            {
                int sum = 0;
                foreach (var zn in loop.ZoneNames)
                    if (channelsByZone.TryGetValue(zn, out int c)) sum += c;

                if (sum > budget)
                {
                    int minLoops = budget > 0 ? (int)Math.Ceiling((double)sum / budget) : sum;
                    violations.Add(new OverCapLoop(loop.Name, sum, budget, minLoops));
                }
            }
            return violations;
        }

        /// <summary>
        /// Every run drawn too long for its zone's coupled feed cap (no throw) — for a UI "redraw these"
        /// list. Empty ⇒ the drawing honors the contract. Assumes mappability (skips unmappable zones,
        /// which Pass 1 of <see cref="Validate"/> owns).
        /// </summary>
        public static IReadOnlyList<OverCapRun> FindOverCapRuns(DmxContract contract, IReadOnlyList<ZoneDesign> zones)
        {
            if (contract == null) throw new ArgumentNullException(nameof(contract));
            if (zones == null) throw new ArgumentNullException(nameof(zones));

            var violations = new List<OverCapRun>();
            foreach (var zone in zones)
            {
                if (zone.Runs.Count == 0) continue;
                int channels = DecoderPacker.SingleChannelsOf(zone.Runs);
                var decoder = DecoderSelector.SelectForChannels(contract.DecoderPool, channels);
                if (decoder is null) continue; // mappability is Pass 1's job

                double cap = PowerPacker.CoupledDecoderCap(decoder.Value, channels, contract.SystemVolts, contract.DriverPool);
                for (int i = 0; i < zone.Runs.Count; i++)
                {
                    var run = zone.Runs[i];
                    double watts = PowerMath.TotalWatts(run);
                    if (watts <= cap + Eps) continue;

                    int minPieces = (int)Math.Ceiling(watts / cap);
                    double maxLen = run.WattsPerFt > 0 ? cap / run.WattsPerFt : 0.0;
                    violations.Add(new OverCapRun(zone.ZoneName, i, watts, run.LengthFt, cap, minPieces, maxLen));
                }
            }
            return violations;
        }
    }
}
