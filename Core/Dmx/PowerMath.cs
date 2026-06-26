namespace TurboSuite.Dmx
{
    /// <summary>
    /// A single physical tape/fixture run as the engine sees it: a length of tape at a known
    /// watts-per-foot and a DMX CHANNEL COUNT (read from the family — no color model in code, §1.5).
    /// Geometry (homeruns, injection points, voltage drop) is deliberately NOT modeled — manual (§3b).
    /// </summary>
    public readonly struct TapeRun
    {
        public TapeRun(double lengthFt, double wattsPerFt, int channels)
        {
            LengthFt = lengthFt;
            WattsPerFt = wattsPerFt;
            Channels = channels;
        }

        public double LengthFt { get; }
        public double WattsPerFt { get; }

        /// <summary>DMX channels this run consumes (= decoder outputs needed, = even-split divisor).</summary>
        public int Channels { get; }
    }

    /// <summary>
    /// The lowest rung of the power engine: per-run watts and per-color current. Pure arithmetic over
    /// a <see cref="TapeRun"/> plus the operating voltage. These feed the decoder caps C1/C2 and tier B.
    /// </summary>
    public static class PowerMath
    {
        /// <summary>Total real watts the run draws. Tier-A (definitional): length × W/ft.</summary>
        public static double TotalWatts(TapeRun run) => run.LengthFt * run.WattsPerFt;

        /// <summary>
        /// Watts on each color terminal under the EVEN split: total ÷ channel count. (Unequal/
        /// white-heavy tape is the known §1.5 leak — a future per-fixture split override, not here.)
        /// </summary>
        public static double PerColorWatts(TapeRun run) => TotalWatts(run) / run.Channels;

        /// <summary>Current on each color terminal — checked against the decoder's per-color cap (C1).</summary>
        public static double PerColorAmps(TapeRun run, double operatingVolts)
            => PerColorWatts(run) / operatingVolts;
    }
}
