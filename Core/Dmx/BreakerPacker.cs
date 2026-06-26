using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx
{
    /// <summary>
    /// Which watts the breaker pack charges per driver (§0c). <see cref="ConnectedLoad"/> = actual draw
    /// (steady-state, the tighter count). <see cref="DriverRating"/> = full nameplate (worst-case, and
    /// the inrush-honest basis — turn-on surge scales with a supply's capacity, not its load).
    /// </summary>
    public enum BreakerBasis { ConnectedLoad, DriverRating }

    /// <summary>
    /// One 120 V branch breaker's assigned drivers — the watts each driver actually DRAWS (its connected
    /// load, not its nameplate rating: a power supply pulls to load, not to capacity, §0c). Realized in
    /// Revit as one unassigned "switched" circuit (no panel/breaker) drawn as a feed on the one-line.
    /// </summary>
    public sealed class BreakerLoad
    {
        private readonly List<double> _driverWatts = new List<double>();

        internal void Add(double watts) => _driverWatts.Add(watts);

        public IReadOnlyList<double> DriverWatts => _driverWatts;
        public int DriverCount => _driverWatts.Count;
        public double TotalWatts => _driverWatts.Sum();
        public double Remaining(double cap) => cap - TotalWatts;
    }

    /// <summary>
    /// The 120 V feed pass (§0c / §6c): pack drivers onto branch breakers by their CONNECTED LOAD watts
    /// under TWO co-equal limits — the watt cap (amps × volts × continuous-derate) AND a max-drivers-per-
    /// breaker count cap (inrush). For a few large drivers watts bind; for many small drivers the count
    /// binds (a 52 W driver draws 52 W, so dozens "fit" by watts but their combined surge would trip).
    /// Atomic FFD: a driver sits on exactly one breaker. The engine emits the COUNT; circuit creation +
    /// the one-line feed are the deferred Revit half.
    /// </summary>
    public static class BreakerPacker
    {
        private const double Eps = 1e-9;

        /// <summary>The breaker watt cap: amps × volts × continuous-derate (the NEC 80% rule by default).</summary>
        public static double Cap(double breakerAmps, double feedVolts, double continuousDerateRaw)
            => breakerAmps * feedVolts * DeratingFactor.Normalize(continuousDerateRaw);

        /// <summary>
        /// Pack driver load watts onto breakers (First-Fit-Decreasing) under the watt cap AND the
        /// per-breaker count cap (<paramref name="maxPerBreaker"/> ≤ 0 ⇒ no count limit). A single driver
        /// whose load exceeds the cap throws — the breaker is too small for the driver pool.
        /// </summary>
        public static IReadOnlyList<BreakerLoad> Pack(IReadOnlyList<double> driverLoadWatts, double cap, int maxPerBreaker)
        {
            if (driverLoadWatts == null) throw new ArgumentNullException(nameof(driverLoadWatts));
            if (cap <= 0) throw new ArgumentOutOfRangeException(nameof(cap), "Breaker watt cap must be positive.");

            var breakers = new List<BreakerLoad>();
            foreach (double w in driverLoadWatts.OrderByDescending(x => x))
            {
                if (w > cap + Eps)
                    throw new InvalidOperationException(
                        $"Driver load {w:F0} W exceeds the breaker cap {cap:F0} W — the breaker is too small "
                        + "for the driver pool. Raise the breaker amps or lower the driver size.");

                BreakerLoad? target = null;
                foreach (var b in breakers)
                {
                    bool wattsFit = w <= b.Remaining(cap) + Eps;
                    bool countFits = maxPerBreaker <= 0 || b.DriverCount < maxPerBreaker;
                    if (wattsFit && countFits) { target = b; break; }
                }
                if (target == null) { target = new BreakerLoad(); breakers.Add(target); }
                target.Add(w);
            }
            return breakers;
        }

        /// <summary>Just the breaker count — the engine's only required 120 V output (§0c).</summary>
        public static int Count(IReadOnlyList<double> driverLoadWatts, double cap, int maxPerBreaker)
            => Pack(driverLoadWatts, cap, maxPerBreaker).Count;
    }
}
