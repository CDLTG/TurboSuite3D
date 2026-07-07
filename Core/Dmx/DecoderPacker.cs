using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx
{
    /// <summary>
    /// One decoder's assigned load: the watt pieces packed onto it. Each piece is a WHOLE run — the
    /// drawn-correctly contract forbids cutting a drawn run, so over-cap runs are flagged
    /// upstream by <see cref="DmxValidator"/> and redrawn, never split here. Geometry/homeruns are not
    /// modeled.
    /// </summary>
    public sealed class DecoderLoad
    {
        private readonly List<double> _pieceWatts = new List<double>();

        internal void Add(double watts) => _pieceWatts.Add(watts);

        public IReadOnlyList<double> PieceWatts => _pieceWatts;
        public double TotalWatts => _pieceWatts.Sum();
    }

    /// <summary>
    /// Pack one zone's runs (uniform channel count) into the fewest decoders under the caps. The
    /// per-color current cap (C1) and total-watt cap (C2) collapse into one effective watt cap, so
    /// binning by watts enforces both. The coupling (PowerPacker) lowers the cap via <see cref="PackToCap"/>.
    /// </summary>
    public static class DecoderPacker
    {
        private const double Eps = 1e-9;

        /// <summary>
        /// The binding watt cap for one decoder driving <paramref name="channels"/> at
        /// <paramref name="volts"/>: min(C2 total-watt cap, C1 per-color amps × volts × channels).
        /// </summary>
        public static double EffectiveWattCap(DecoderSpec decoder, int channels, double volts)
        {
            double perColorWattCap = decoder.MaxAmpsPerOutput * volts * channels;
            return Math.Min(decoder.MaxWatts, perColorWattCap);
        }

        /// <summary>Pack runs into decoder loads under the decoder's own caps.</summary>
        public static IReadOnlyList<DecoderLoad> Pack(IReadOnlyList<TapeRun> runs, DecoderSpec decoder, double volts)
        {
            if (runs == null) throw new ArgumentNullException(nameof(runs));
            if (runs.Count == 0) return Array.Empty<DecoderLoad>();
            return PackToCap(runs, EffectiveWattCap(decoder, SingleChannelsOf(runs), volts));
        }

        /// <summary>The one channel count shared by all runs; throws if they disagree (mixed is a later step).</summary>
        public static int SingleChannelsOf(IReadOnlyList<TapeRun> runs)
        {
            var counts = runs.Select(r => r.Channels).Distinct().ToList();
            if (counts.Count > 1)
                throw new ArgumentException("Packing requires runs of a single channel count; mixed-channel packing is a later step.");
            return counts[0];
        }

        /// <summary>
        /// Group WHOLE runs into decoder loads under an explicit watt cap using First-Fit-Decreasing.
        /// Conserves watts. The drawn-correctly contract: a run that exceeds the cap is NOT
        /// split — it throws (a backstop; <see cref="DmxValidator"/> flags it upstream for redraw). The
        /// coupling entry point.
        /// </summary>
        public static IReadOnlyList<DecoderLoad> PackToCap(IReadOnlyList<TapeRun> runs, double cap)
        {
            if (runs == null) throw new ArgumentNullException(nameof(runs));
            if (runs.Count == 0) return Array.Empty<DecoderLoad>();
            if (cap <= 0) throw new ArgumentOutOfRangeException(nameof(cap), "Decoder watt cap must be positive.");

            var pieces = new List<double>();
            foreach (var run in runs)
            {
                double watts = PowerMath.TotalWatts(run);
                if (watts > cap + Eps)
                    throw new InvalidOperationException(
                        $"Run {watts:F0} W exceeds the feed cap {cap:F0} W. The drawn-correctly contract "
                        + "forbids silently splitting a drawn run — over-cap runs must be "
                        + "flagged by DmxValidator and redrawn, not cut.");
                pieces.Add(watts);
            }

            pieces.Sort((a, b) => b.CompareTo(a)); // descending

            var loads = new List<DecoderLoad>();
            var remaining = new List<double>();
            foreach (double piece in pieces)
            {
                int target = -1;
                for (int i = 0; i < remaining.Count; i++)
                {
                    if (piece <= remaining[i] + Eps) { target = i; break; }
                }
                if (target < 0)
                {
                    var load = new DecoderLoad();
                    load.Add(piece);
                    loads.Add(load);
                    remaining.Add(cap - piece);
                }
                else
                {
                    loads[target].Add(piece);
                    remaining[target] -= piece;
                }
            }

            return loads;
        }
    }
}
