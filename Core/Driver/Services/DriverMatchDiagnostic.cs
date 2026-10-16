#nullable disable
using System;
using System.Linq;
using TurboSuite.Driver.Models;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Explains, field by field, why a given driver type does or doesn't satisfy a given RPS fixture
    /// type — the read-only companion to <see cref="DriverSelectionService"/>. It reproduces exactly
    /// the hard gates that service applies per candidate, reduced to the single-fixture-type case the
    /// match-check window compares:
    ///
    /// <list type="number">
    /// <item><b>Valid driver</b> — Power / Sub-Driver Power via the shared <see cref="DriverValidity"/>
    /// (the one place the rule lives).</item>
    /// <item><b>Voltage</b> — driver's must be non-blank and equal the fixture's (case-insensitive).
    /// Blank is NOT a wildcard. A fixture with no Voltage can't match any real driver.</item>
    /// <item><b>Dimming Protocol</b> — same rule as Voltage.</item>
    /// </list>
    ///
    /// plus the one soft preference the matcher ranks on (never a gate):
    /// <list type="bullet"><item><b>Manufacturer</b> — a match is preferred; a mismatch still
    /// matches.</item></list>
    ///
    /// The TBD placeholder is the single wildcard: it bypasses every hard gate and always matches.
    ///
    /// LOCKSTEP: these checks must track <see cref="DriverSelectionService"/>.EvaluateCandidate. If a
    /// new hard filter is ever added to the matcher, add a hard-gate check here too (validity itself
    /// is already shared through <see cref="DriverValidity"/>).
    /// </summary>
    public static class DriverMatchDiagnostic
    {
        private const string Blank = "(blank)";
        private const string NA = "—";

        public static DriverMatchReport Compare(FixtureData fixture, DriverCandidateInfo driver)
        {
            var report = new DriverMatchReport();
            if (fixture == null || driver == null)
            {
                report.Matches = false;
                report.Summary = "Select a driver and an RPS fixture to compare.";
                return report;
            }

            // 1. Valid driver (hard gate). TBD is the wildcard — report it, but it never fails.
            if (driver.IsTbd)
            {
                report.Checks.Add(new MatchCheck
                {
                    Field = "Driver type",
                    FixtureValue = NA,
                    DriverValue = "TBD placeholder",
                    Verdict = MatchVerdict.Info,
                    Reason = "wildcard — matches any circuit; a real driver is preferred when one fits",
                    IsHardGate = false
                });
            }
            else
            {
                var validity = DriverValidity.Evaluate(driver.TotalPower, driver.SubDriverPower);
                report.Checks.Add(new MatchCheck
                {
                    Field = "Valid driver",
                    FixtureValue = NA,
                    DriverValue = validity.IsValid
                        ? $"{Fmt(driver.TotalPower)} W ÷ {Fmt(driver.SubDriverPower)} W = {validity.SubDriverCount} sub-driver(s)"
                        : $"Power {Fmt(driver.TotalPower)} W / Sub-Driver {Fmt(driver.SubDriverPower)} W",
                    Verdict = validity.IsValid ? MatchVerdict.Pass : MatchVerdict.Fail,
                    Reason = validity.Reason,
                    IsHardGate = true
                });
            }

            // 2 & 3. Voltage and Dimming Protocol (hard gates).
            report.Checks.Add(SpecGate("Voltage", fixture.Voltage, driver.Voltage, driver.IsTbd));
            report.Checks.Add(SpecGate("Dimming Protocol", fixture.DimmingProtocol, driver.DimmingProtocol, driver.IsTbd));

            // 4. Manufacturer (soft — ranks, never blocks).
            report.Checks.Add(ManufacturerCheck(fixture.Manufacturer, driver.Manufacturer, driver.IsTbd));

            // 5. Wattage / capacity (info only — a linear fixture legitimately carries no type wattage).
            report.Checks.Add(WattageInfo(fixture, driver));

            var firstFail = report.Checks.FirstOrDefault(c => c.IsHardGate && c.Verdict == MatchVerdict.Fail);
            report.Matches = firstFail == null;

            if (!report.Matches)
                report.Summary = $"✗ Won't match — {firstFail.Field}: {firstFail.Reason}";
            else if (driver.IsTbd)
                report.Summary = "✓ Matches — TBD placeholder (wildcard). A real driver is preferred when one fits.";
            else if (report.Checks.Any(c => c.Field == "Manufacturer" && c.Verdict == MatchVerdict.Warn))
                report.Summary = "✓ Matches — but Manufacturer differs; a same-manufacturer driver is preferred when one fits.";
            else
                report.Summary = "✓ Matches";

            return report;
        }

        /// <summary>A hard-gate spec field (Voltage / Dimming Protocol). TBD ignores it; otherwise both
        /// sides must be non-blank and equal (case-insensitive) — blank is never a wildcard.</summary>
        private static MatchCheck SpecGate(string field, string fixtureValue, string driverValue, bool isTbd)
        {
            string fv = Show(fixtureValue);
            string dv = Show(driverValue);

            if (isTbd)
                return Hard(field, fv, "TBD", MatchVerdict.Info, $"TBD ignores {field}");

            if (string.IsNullOrWhiteSpace(fixtureValue))
                return Hard(field, fv, dv, MatchVerdict.Fail,
                    $"fixture has no {field} declared — no real driver can match until it does");

            if (string.IsNullOrWhiteSpace(driverValue))
                return Hard(field, fv, dv, MatchVerdict.Fail,
                    $"driver {field} is blank — blank is not a wildcard; it must declare and match");

            if (string.Equals(fixtureValue.Trim(), driverValue.Trim(), StringComparison.OrdinalIgnoreCase))
                return Hard(field, fv, dv, MatchVerdict.Pass, null);

            return Hard(field, fv, dv, MatchVerdict.Fail, "values differ");
        }

        private static MatchCheck ManufacturerCheck(string fixtureMfr, string driverMfr, bool isTbd)
        {
            string fv = Show(fixtureMfr);
            string dv = Show(driverMfr);

            if (isTbd)
                return Soft("Manufacturer", fv, "TBD", MatchVerdict.Info, "TBD ignores Manufacturer");

            if (string.IsNullOrWhiteSpace(fixtureMfr) || string.IsNullOrWhiteSpace(driverMfr))
                return Soft("Manufacturer", fv, dv, MatchVerdict.Warn,
                    "blank on one side — still matches, but a same-manufacturer driver can't be confirmed");

            if (string.Equals(fixtureMfr.Trim(), driverMfr.Trim(), StringComparison.OrdinalIgnoreCase))
                return Soft("Manufacturer", fv, dv, MatchVerdict.Pass, null);

            return Soft("Manufacturer", fv, dv, MatchVerdict.Warn,
                "differs — still matches, but a same-manufacturer driver is preferred when one fits");
        }

        private static MatchCheck WattageInfo(FixtureData fixture, DriverCandidateInfo driver)
        {
            double fixtureW = fixture.EffectiveWattage;
            string fixtureText = fixtureW > 0
                ? $"{Fmt(fixtureW)} W"
                : "0 W (linear fixtures carry wattage per-foot on the instance)";

            string driverText;
            if (driver.IsTbd || driver.SubDriverPower <= 0)
            {
                driverText = NA;
            }
            else
            {
                double derate = driver.DerateFactor > 0 ? driver.DerateFactor : 1.0;
                double cap = driver.SubDriverPower * derate;
                driverText = derate < 1.0
                    ? $"{Fmt(cap)} W per sub-driver (derated from {Fmt(driver.SubDriverPower)} W)"
                    : $"{Fmt(driver.SubDriverPower)} W per sub-driver";
            }

            return new MatchCheck
            {
                Field = "Wattage",
                FixtureValue = fixtureText,
                DriverValue = driverText,
                Verdict = MatchVerdict.Info,
                Reason = fixtureW > 0
                    ? "informational — an oversized fixture splits across sub-drivers; never a mismatch"
                    : null,
                IsHardGate = false
            };
        }

        private static MatchCheck Hard(string field, string fv, string dv, MatchVerdict v, string reason)
            => new MatchCheck { Field = field, FixtureValue = fv, DriverValue = dv, Verdict = v, Reason = reason, IsHardGate = true };

        private static MatchCheck Soft(string field, string fv, string dv, MatchVerdict v, string reason)
            => new MatchCheck { Field = field, FixtureValue = fv, DriverValue = dv, Verdict = v, Reason = reason, IsHardGate = false };

        private static string Show(string v) => string.IsNullOrWhiteSpace(v) ? Blank : v.Trim();
        private static string Fmt(double w) => w.ToString("0.##");
    }
}
