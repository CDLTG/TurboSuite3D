namespace TurboSuite.Dmx
{
    /// <summary>
    /// Wire gauges that fall out of the channel count (§8a): channels + 1 common, rounded up to stock
    /// pairs ⇒ #16-2 (1 ch) / #16-4 (2–3 ch) / #16-6 (4–5 ch) / #16-8 (6 ch RGBATW) / … — <b>uncapped</b>
    /// (BuildPlan Phase 6; the job-wide pull-up lives in <see cref="OneLine.DmxWireLegend"/>). The HV
    /// breaker→driver feed is #12-2. Derived from the count, not hardcoded per color model.
    /// </summary>
    public static class WireSpec
    {
        /// <summary>Color conductors plus one common.</summary>
        public static int ConductorsFor(int channels) => channels + 1;

        /// <summary>Conductors rounded up to the next even stock count (2/4/6…).</summary>
        public static int StockConductors(int channels)
        {
            int n = ConductorsFor(channels);
            return (n % 2 == 0) ? n : n + 1;
        }

        /// <summary>The decoder→tape cable, e.g. "#16-6" for 4-channel RGBW.</summary>
        public static string TapeCable(int channels) => "#16-" + StockConductors(channels);

        /// <summary>The HV breaker→driver feed.</summary>
        public const string DriverFeedCable = "#12-2";
    }
}
