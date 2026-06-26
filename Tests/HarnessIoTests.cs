using TurboSuite.Dmx;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>Wire gauges derived from the channel count (§8a): channels + 1 common, rounded to stock pairs.</summary>
    public class WireSpecTests
    {
        [Theory]
        [InlineData(1, "#16-2")]
        [InlineData(2, "#16-4")]
        [InlineData(3, "#16-4")]
        [InlineData(4, "#16-6")]
        [InlineData(5, "#16-6")]
        [InlineData(6, "#16-8")]
        public void TapeCable_DerivesFromChannelCount(int channels, string expected)
        {
            Assert.Equal(expected, WireSpec.TapeCable(channels));
        }
    }

    /// <summary>The scenario file parser and the end-to-end harness path (parse → solve → report).</summary>
    public class ScenarioParserTests
    {
        [Theory]
        [InlineData("66'9", 66.75)]
        [InlineData("66'-9\"", 66.75)]
        [InlineData("42'0", 42.0)]
        [InlineData("23.27", 23.27)]
        public void ParseFeet_HandlesFeetInchesAndDecimal(string text, double expected)
        {
            Assert.Equal(expected, ScenarioParser.ParseFeet(text), precision: 2);
        }

        [Fact]
        public void Parse_ExpandsRunMultiplier()
        {
            const string text = @"
decoder = 4ch outputs:4 amps:10 watts:960
driver = MD 480 24 0.85
zone = Wall | 2 | 17.2 ×72
zone = Mixed | 2 | 17.2 x2, 20, 30*3
";
            var s = ScenarioParser.Parse(text);

            Assert.Equal(72, s.Zones[0].Runs.Count);
            Assert.All(s.Zones[0].Runs, r => Assert.Equal(17.2, r.LengthFt, precision: 2));
            Assert.Equal(6, s.Zones[1].Runs.Count); // 2 + 1 + 3
        }

        [Fact]
        public void Parse_ReadsContractAndZones()
        {
            const string text = @"
volts = 24
ceiling = 32
decoder = 4ch outputs:4 amps:10 watts:960
driver = MD 480 24 0.85
driver = ME 600 24 0.85
zone = Cove | 4 | 66'9, 44'1, 42'0
";
            var s = ScenarioParser.Parse(text);

            Assert.Equal(24, s.Contract.SystemVolts);
            Assert.Single(s.Contract.DecoderPool);
            Assert.Equal(2, s.Contract.DriverPool.Count);
            Assert.Single(s.Zones);
            Assert.Equal("Cove", s.Zones[0].ZoneName);
            Assert.Equal(3, s.Zones[0].Runs.Count);
            Assert.Equal(4, s.Zones[0].Runs[0].Channels);
        }

        [Fact]
        public void ParseSolveReport_ProducesANonEmptyBillDump()
        {
            const string text = @"
ceiling = 32
decoder = 4ch outputs:4 amps:10 watts:960
decoder = 6ch outputs:6 amps:6 watts:864
driver = MD 480 24 0.85
driver = ME 600 24 0.85
zone = Cove | 4 | 66'9, 44'1, 42'0
";
            var s = ScenarioParser.Parse(text);
            var bill = DmxSolver.Solve(s.Contract, s.Zones);
            string report = BillReport.Format(bill, s.Contract);

            Assert.Contains("TurboDMX — solve bill", report);
            Assert.Contains("INTERFACE #1", report);
            Assert.Contains("DEC 1", report);
            Assert.Contains("#16-6", report);  // 4-ch tape cable in the legend
            Assert.Contains("DECODERS (BOM)", report);
            Assert.Contains("120V feeds", report); // the breaker count line
        }
    }
}
