#nullable disable
using System;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// The single source of truth for whether a Lighting Device's Power / Sub-Driver Power make it a
    /// sizeable driver, and — when they don't — a human-readable reason why. Shared by the collector
    /// that builds the candidate pool (<c>FamilyTypeCollectorService</c>) and the read-only
    /// <see cref="DriverMatchDiagnostic"/>, so the two can never disagree about what "valid" means.
    ///
    /// The DMX-decoder guard (DMX Channels &gt; 0) deliberately stays with the collector: that is a
    /// category decision, not a wattage one, and the match-check window never offers decoders.
    /// </summary>
    public static class DriverValidity
    {
        /// <summary>Tolerance (watts) for "Power is a whole multiple of Sub-Driver Power".
        /// Mirrors the historical inline check in the collector.</summary>
        public const double RemainderTolerance = 0.01;

        public static DriverValidityResult Evaluate(double power, double subDriverPower)
        {
            if (power <= 0)
                return DriverValidityResult.Invalid("Power is 0 or unset");
            if (subDriverPower <= 0)
                return DriverValidityResult.Invalid("Sub-Driver Power is 0 or unset");

            double remainder = power % subDriverPower;
            if (Math.Abs(remainder) >= RemainderTolerance)
                return DriverValidityResult.Invalid(
                    $"Power ({Fmt(power)} W) is not a whole multiple of Sub-Driver Power ({Fmt(subDriverPower)} W)");

            int subCount = (int)Math.Round(power / subDriverPower);
            if (subCount <= 0)
                return DriverValidityResult.Invalid("Power is 0 or unset");

            return DriverValidityResult.Valid(subCount);
        }

        private static string Fmt(double w) => w.ToString("0.##");
    }

    /// <summary>Outcome of <see cref="DriverValidity.Evaluate"/>: validity, the derived sub-driver
    /// count (0 when invalid), and the failure reason (null when valid).</summary>
    public class DriverValidityResult
    {
        public bool IsValid { get; private set; }
        public int SubDriverCount { get; private set; }
        public string Reason { get; private set; }

        public static DriverValidityResult Valid(int subCount)
            => new DriverValidityResult { IsValid = true, SubDriverCount = subCount, Reason = null };

        public static DriverValidityResult Invalid(string reason)
            => new DriverValidityResult { IsValid = false, SubDriverCount = 0, Reason = reason };
    }
}
