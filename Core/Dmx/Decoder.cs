using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx
{
    /// <summary>
    /// A decoder type's declared caps (Kind-1 part properties) — read off the family type. The
    /// two power caps are C1 (per-color current) and C2 (total watts); <see cref="MaxOutputs"/> is the
    /// channel ceiling that gates which tape it can drive. <see cref="Name"/> is a label only.
    /// </summary>
    public readonly struct DecoderSpec
    {
        public DecoderSpec(string name, int maxOutputs, double maxAmpsPerOutput, double maxWatts)
        {
            Name = name;
            MaxOutputs = maxOutputs;
            MaxAmpsPerOutput = maxAmpsPerOutput;
            MaxWatts = maxWatts;
        }

        public string Name { get; }

        /// <summary>Physical output channels — the most DMX channels of tape this decoder can drive.</summary>
        public int MaxOutputs { get; }

        /// <summary>C1 — max current on any one color terminal, summed over paralleled homeruns.</summary>
        public double MaxAmpsPerOutput { get; }

        /// <summary>C2 — max total output watts at the operating voltage.</summary>
        public double MaxWatts { get; }

        /// <summary>DMX-4-5000-10A @24 V (datasheet, Tier B): 4 outputs, 10 A/ch, 960 W.</summary>
        public static readonly DecoderSpec Dmx4_5000_10A = new DecoderSpec("4ch (DMX-4-5000-10A)", 4, 10.0, 960.0);

        /// <summary>DMX-6-22K (StudioPro 6 Ch) @24 V (datasheet, Tier B): 6 outputs, 6 A/ch, 864 W.</summary>
        public static readonly DecoderSpec Dmx6_22K = new DecoderSpec("6ch (DMX-6-22K)", 6, 6.0, 864.0);
    }

    /// <summary>
    /// Raised when a zone's tape needs more DMX channels than any decoder in the contract pool can
    /// drive ( "impossible part"). This is a run-breaking contract-configuration error — the Revit
    /// layer surfaces it as a dialog and aborts the whole solve.
    /// </summary>
    public sealed class UnmappableTapeException : Exception
    {
        public UnmappableTapeException(string zoneName, int channelsNeeded, int maxOutputsAvailable)
            : base($"Zone '{zoneName}' needs {channelsNeeded} DMX channels, but the decoder pool maxes at "
                   + $"{maxOutputsAvailable} outputs. Add a {channelsNeeded}+ channel decoder to the contract.")
        {
            ZoneName = zoneName;
            ChannelsNeeded = channelsNeeded;
            MaxOutputsAvailable = maxOutputsAvailable;
        }

        public string ZoneName { get; }
        public int ChannelsNeeded { get; }
        public int MaxOutputsAvailable { get; }
    }

    /// <summary>
    /// The outcome of checking a load against a decoder's caps. Exposes each cap separately so
    /// callers (and tests) can see exactly which one binds, not just pass/fail.
    /// </summary>
    public readonly struct DecoderFitResult
    {
        public DecoderFitResult(bool withinOutputs, bool withinPerColorCurrent, bool withinTotalWatts,
                                double perColorAmps, double totalWatts)
        {
            WithinOutputs = withinOutputs;
            WithinPerColorCurrent = withinPerColorCurrent;
            WithinTotalWatts = withinTotalWatts;
            PerColorAmps = perColorAmps;
            TotalWatts = totalWatts;
        }

        public bool WithinOutputs { get; }
        public bool WithinPerColorCurrent { get; }
        public bool WithinTotalWatts { get; }
        public double PerColorAmps { get; }
        public double TotalWatts { get; }

        public bool Fits => WithinOutputs && WithinPerColorCurrent && WithinTotalWatts;
    }

    /// <summary>Does a load fit one decoder? Checks outputs (≥ channels), C1 (per-color amps) and C2 (watts).</summary>
    public static class DecoderFit
    {
        public static DecoderFitResult Check(DecoderSpec decoder, double totalWatts, double perColorAmps, int channels)
            => new DecoderFitResult(
                withinOutputs: channels <= decoder.MaxOutputs,
                withinPerColorCurrent: perColorAmps <= decoder.MaxAmpsPerOutput,
                withinTotalWatts: totalWatts <= decoder.MaxWatts,
                perColorAmps: perColorAmps,
                totalWatts: totalWatts);

        public static DecoderFitResult Check(DecoderSpec decoder, TapeRun run, double operatingVolts)
            => Check(decoder, PowerMath.TotalWatts(run), PowerMath.PerColorAmps(run, operatingVolts), run.Channels);
    }

    /// <summary>
    /// Selects the decoder TYPE for a zone by channel need: the smallest-output decoder whose
    /// <see cref="DecoderSpec.MaxOutputs"/> ≥ the tape's channels. Never mixes capacity tiers — it's
    /// purely output-count matching (4-ch tape → 4-ch decoder; 5/6-ch tape → 6-ch decoder).
    /// </summary>
    public static class DecoderSelector
    {
        /// <summary>Smallest-output decoder that can drive <paramref name="channels"/>; null if none can.</summary>
        public static DecoderSpec? SelectForChannels(IReadOnlyList<DecoderSpec> pool, int channels)
        {
            var fitting = pool.Where(d => d.MaxOutputs >= channels).OrderBy(d => d.MaxOutputs).ToList();
            return fitting.Count == 0 ? (DecoderSpec?)null : fitting[0];
        }

        /// <summary>Largest output count in the pool (for the blocker message). 0 if the pool is empty.</summary>
        public static int MaxOutputs(IReadOnlyList<DecoderSpec> pool)
            => pool.Count == 0 ? 0 : pool.Max(d => d.MaxOutputs);
    }
}
