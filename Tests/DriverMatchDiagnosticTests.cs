using System.Linq;
using TurboSuite.Driver.Models;
using TurboSuite.Driver.Services;
using Xunit;

namespace TurboSuite.Tests.Driver
{
    /// <summary>
    /// Oracles for <see cref="DriverMatchDiagnostic"/>. These pin the SAME hard gates
    /// <see cref="DriverSelectionService"/> applies, reduced to one fixture type vs one driver type:
    /// valid driver, Voltage, Dimming Protocol (hard); Manufacturer (soft); TBD wildcard; wattage info.
    /// </summary>
    public class DriverMatchDiagnosticTests
    {
        private static FixtureData Fixture(string voltage = "24V", string dimming = "0-10V",
            string mfr = "AcmeLux", double typePower = 30)
            => new FixtureData
            {
                TypePower = typePower,
                LinearPower = 0,
                Voltage = voltage,
                DimmingProtocol = dimming,
                Manufacturer = mfr,
                HasRemotePowerSupply = true
            };

        private static DriverCandidateInfo Driver(string voltage = "24V", string dimming = "0-10V",
            string mfr = "AcmeLux", double power = 300, double sub = 100, bool isTbd = false)
            => new DriverCandidateInfo
            {
                Voltage = voltage,
                DimmingProtocol = dimming,
                Manufacturer = mfr,
                TotalPower = power,
                SubDriverPower = sub,
                DerateFactor = 1.0,
                IsTbd = isTbd
            };

        private static MatchCheck Check(DriverMatchReport r, string field)
            => r.Checks.Single(c => c.Field == field);

        [Fact]
        public void FullSpecMatch_Matches()
        {
            var r = DriverMatchDiagnostic.Compare(Fixture(), Driver());
            Assert.True(r.Matches);
            Assert.Equal(MatchVerdict.Pass, Check(r, "Voltage").Verdict);
            Assert.Equal(MatchVerdict.Pass, Check(r, "Dimming Protocol").Verdict);
            Assert.Equal(MatchVerdict.Pass, Check(r, "Manufacturer").Verdict);
            Assert.Equal(MatchVerdict.Pass, Check(r, "Valid driver").Verdict);
        }

        [Fact]
        public void VoltageMismatch_HardFails()
        {
            var r = DriverMatchDiagnostic.Compare(Fixture(voltage: "24V"), Driver(voltage: "120V"));
            Assert.False(r.Matches);
            var v = Check(r, "Voltage");
            Assert.Equal(MatchVerdict.Fail, v.Verdict);
            Assert.True(v.IsHardGate);
        }

        [Fact]
        public void VoltageMatchIsCaseInsensitive()
        {
            var r = DriverMatchDiagnostic.Compare(Fixture(voltage: "24v"), Driver(voltage: "24V"));
            Assert.Equal(MatchVerdict.Pass, Check(r, "Voltage").Verdict);
            Assert.True(r.Matches);
        }

        [Fact]
        public void BlankDriverVoltageIsNotWildcard_Fails()
        {
            var r = DriverMatchDiagnostic.Compare(Fixture(voltage: "24V"), Driver(voltage: ""));
            Assert.False(r.Matches);
            Assert.Equal(MatchVerdict.Fail, Check(r, "Voltage").Verdict);
        }

        [Fact]
        public void FixtureMissingVoltage_FailsForRealDriver()
        {
            var r = DriverMatchDiagnostic.Compare(Fixture(voltage: ""), Driver());
            Assert.False(r.Matches);
            Assert.Contains("fixture has no Voltage", Check(r, "Voltage").Reason);
        }

        [Fact]
        public void DimmingProtocolMismatch_HardFails()
        {
            var r = DriverMatchDiagnostic.Compare(Fixture(dimming: "0-10V"), Driver(dimming: "ELV"));
            Assert.False(r.Matches);
            Assert.Equal(MatchVerdict.Fail, Check(r, "Dimming Protocol").Verdict);
        }

        [Fact]
        public void ManufacturerDiffers_StillMatches_WithWarn()
        {
            var r = DriverMatchDiagnostic.Compare(Fixture(mfr: "AcmeLux"), Driver(mfr: "OtherCo"));
            Assert.True(r.Matches);
            Assert.Equal(MatchVerdict.Warn, Check(r, "Manufacturer").Verdict);
            Assert.False(Check(r, "Manufacturer").IsHardGate);
            Assert.Contains("Manufacturer differs", r.Summary);
        }

        [Fact]
        public void InvalidDriver_NonMultiplePower_HardFails()
        {
            var r = DriverMatchDiagnostic.Compare(Fixture(), Driver(power: 300, sub: 90));
            Assert.False(r.Matches);
            var valid = Check(r, "Valid driver");
            Assert.Equal(MatchVerdict.Fail, valid.Verdict);
            Assert.True(valid.IsHardGate);
        }

        [Fact]
        public void Tbd_MatchesEvenWhenSpecsDiffer()
        {
            // TBD is the wildcard: bypasses Voltage / Dimming / validity entirely.
            var r = DriverMatchDiagnostic.Compare(
                Fixture(voltage: "24V", dimming: "0-10V"),
                Driver(voltage: "120V", dimming: "ELV", power: 0, sub: 0, isTbd: true));
            Assert.True(r.Matches);
            Assert.Contains("TBD", r.Summary);
        }

        [Fact]
        public void Tbd_MatchesEvenWhenFixtureUnderspecified()
        {
            var r = DriverMatchDiagnostic.Compare(
                Fixture(voltage: "", dimming: ""),
                Driver(isTbd: true));
            Assert.True(r.Matches);
        }

        [Fact]
        public void Wattage_IsInfoOnly_NeverGates()
        {
            // A linear fixture with 0 type wattage must not block the match.
            var r = DriverMatchDiagnostic.Compare(Fixture(typePower: 0), Driver());
            Assert.True(r.Matches);
            Assert.Equal(MatchVerdict.Info, Check(r, "Wattage").Verdict);
            Assert.False(Check(r, "Wattage").IsHardGate);
        }
    }
}
