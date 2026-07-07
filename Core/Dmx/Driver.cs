using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx
{
    /// <summary>
    /// The derate convention, identical to TurboDriver's documented <c>Derating Factor</c> rule:
    /// the max fraction of rated capacity to load to. A raw value of missing / 0 / out-of-range
    /// means NO derate (1.0). Applied only to the packing ceiling, never to count math.
    /// </summary>
    public static class DeratingFactor
    {
        /// <summary>Clamp a raw factor to its effective value: anything ≤0 or &gt;1 ⇒ 1.0 (no derate).</summary>
        public static double Normalize(double raw) => (raw <= 0.0 || raw > 1.0) ? 1.0 : raw;
    }

    /// <summary>
    /// A driver Type as the engine sees it (Kind-1 part properties). <see cref="Name"/> is a
    /// label only — role/watts come from parameters, never from parsing the Type-mark string
    /// (device-identity rule). Rated watts and the derating factor are read off the family Type.
    /// </summary>
    public readonly struct DriverType
    {
        public DriverType(string name, double ratedWatts, double operatingVolts, double deratingFactorRaw)
        {
            Name = name;
            RatedWatts = ratedWatts;
            OperatingVolts = operatingVolts;
            DeratingFactorRaw = deratingFactorRaw;
        }

        public string Name { get; }
        public double RatedWatts { get; }
        public double OperatingVolts { get; }

        /// <summary>The factor as read from the family parameter, before normalization.</summary>
        public double DeratingFactorRaw { get; }

        /// <summary>Effective derate after the missing/0/out-of-range ⇒ 1.0 rule.</summary>
        public double EffectiveDerate => DeratingFactor.Normalize(DeratingFactorRaw);

        /// <summary>The watts this Type may actually be loaded to: ratedW × effective derate.</summary>
        public double EffectiveWattCap => RatedWatts * EffectiveDerate;
    }

    /// <summary>
    /// Step 4 — driver selection: from a family's candidate Types, pick the smallest whose
    /// effective cap covers the load, after filtering to system voltage. Mirrors TurboDriver's
    /// "family of Types, smallest that fits." Returns null when no single Type covers the load —
    /// the caller then splits into more drivers (over-grouping contract).
    /// </summary>
    public static class DriverSelector
    {
        private const double Eps = 1e-9;

        /// <summary>Candidate Types whose operating voltage matches the system voltage.</summary>
        public static IReadOnlyList<DriverType> CandidatesForVoltage(IReadOnlyList<DriverType> all, double systemVolts)
            => all.Where(t => System.Math.Abs(t.OperatingVolts - systemVolts) < 1e-6).ToList();

        /// <summary>
        /// Smallest (by rated watts) voltage-matched Type whose <see cref="DriverType.EffectiveWattCap"/>
        /// is at least <paramref name="loadWatts"/>; null if none fits.
        /// </summary>
        public static DriverType? SelectSmallestFitting(IReadOnlyList<DriverType> candidates, double loadWatts, double systemVolts)
        {
            var fitting = CandidatesForVoltage(candidates, systemVolts)
                .Where(t => t.EffectiveWattCap >= loadWatts - Eps)
                .OrderBy(t => t.RatedWatts)
                .ToList();
            return fitting.Count == 0 ? (DriverType?)null : fitting[0];
        }

        /// <summary>
        /// The largest effective cap among voltage-matched candidates — the bound the decoder
        /// packing ceiling is clamped to (decision A coupling: decoder load ≤ min(960, this)).
        /// 0 if no candidate matches the voltage.
        /// </summary>
        public static double LargestEffectiveCap(IReadOnlyList<DriverType> candidates, double systemVolts)
        {
            var matched = CandidatesForVoltage(candidates, systemVolts);
            return matched.Count == 0 ? 0.0 : matched.Max(t => t.EffectiveWattCap);
        }
    }
}
