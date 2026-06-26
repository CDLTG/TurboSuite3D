using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// The whole pipeline in one call. A small hand-verifiable solve, the §6a bracket, decoder-type
    /// selection (4-ch vs 6-ch), and the run-breaking blocker. Also the I/O-testing substrate.
    /// </summary>
    public class DmxSolverTests
    {
        private const double V = 24.0;

        private static DriverType[] Drivers(double derate) => new[]
        {
            new DriverType("320", 320.0, V, derate),
            new DriverType("480", 480.0, V, derate),
            new DriverType("600", 600.0, V, derate),
        };

        // Pool with both decoder tiers, unless a test overrides it.
        private static DmxContract Contract(double derate, int ceiling = 32, int reserved = 0, int d4 = 32,
                                            IReadOnlyList<DecoderSpec>? decoders = null) =>
            new DmxContract(
                decoderPool: decoders ?? new[] { DecoderSpec.Dmx4_5000_10A, DecoderSpec.Dmx6_22K },
                driverPool: Drivers(derate),
                systemVolts: V,
                channelCeiling: ceiling,
                reservedChannels: reserved,
                maxDevicesPerSegment: d4);

        [Fact]
        public void FoyerCove_OneZone_AtDerate85_FullBill()
        {
            // 794.7 W RGBW, coupled cap @0.85 = 510 ⇒ 2 decoders (FFD bins [347] and [229+218=448]):
            // 347 ⇒ 480 driver, 448 ⇒ 600 driver. 4-ch tape ⇒ the 4ch decoder. 1 interface, no repeater.
            var zones = new[]
            {
                new ZoneDesign("Foyer Cove", new[]
                {
                    new TapeRun(66.75,   5.2, 4),
                    new TapeRun(44.0833, 5.2, 4),
                    new TapeRun(42.0,    5.2, 4),
                }),
            };

            var bill = DmxSolver.Solve(Contract(0.85), zones);

            Assert.Equal(2, bill.TotalDecoders);
            Assert.Equal(new Dictionary<string, int> { ["4ch (DMX-4-5000-10A)"] = 2 }, bill.DecodersByType);
            Assert.Equal(new Dictionary<string, int> { ["480"] = 1, ["600"] = 1 }, bill.DriversByType);
            Assert.Equal(4, bill.TotalChannels);
            Assert.Equal(1, bill.InterfaceCount);
            Assert.Equal(0, bill.TotalRepeaters);
            Assert.Equal(794.73, bill.TotalWatts, precision: 1);
        }

        [Fact]
        public void RequiredBreakers_LoadPacksDrivers_AndObeysTheInrushCap()
        {
            // 6 zones × 300 W ⇒ 6 drivers each carrying 300 W of LOAD. By watts all 6 fit one 1920 W
            // breaker (1800 W), but the inrush cap of 4 drivers/breaker forces 2 — the count binds.
            var zones = Enumerable.Range(0, 6)
                .Select(i => new ZoneDesign($"z{i}", new[] { new TapeRun(300.0 / 5.2, 5.2, 4) }))
                .ToArray();
            var contract = new DmxContract(
                decoderPool: new[] { DecoderSpec.Dmx4_5000_10A, DecoderSpec.Dmx6_22K },
                driverPool: Drivers(1.0), systemVolts: V, channelCeiling: 512, reservedChannels: 0,
                maxDevicesPerSegment: 32, breakerAmps: 20, feedVolts: 120, breakerContinuousDerate: 0.8,
                maxDriversPerBreaker: 4);

            var bill = DmxSolver.Solve(contract, zones);

            Assert.Equal(6, bill.TotalDrivers);
            Assert.Equal(2, bill.RequiredBreakers);
            // Feeds carry connected LOAD, not nameplate: Σ breaker watts == Σ decoder watts (6 × 300).
            Assert.Equal(1800.0, bill.Breakers.Sum(b => b.TotalWatts), precision: 0);
        }

        // --- Decoder-type selection by channel need ---

        [Fact]
        public void FiveChannelTape_SelectsTheSixChannelDecoder()
        {
            var zones = new[] { new ZoneDesign("RGBTW Cove", new[] { new TapeRun(40.0, 5.2, channels: 5) }) };

            var bill = DmxSolver.Solve(Contract(0.85), zones);

            Assert.Equal(new Dictionary<string, int> { ["6ch (DMX-6-22K)"] = 1 }, bill.DecodersByType);
            Assert.Equal(5, bill.TotalChannels); // rgb(3)+cool(1)+warm(1)
        }

        [Fact]
        public void FourChannelTape_PrefersTheSmallerFourChannelDecoder()
        {
            var zones = new[] { new ZoneDesign("RGBW", new[] { new TapeRun(40.0, 5.2, channels: 4) }) };

            var bill = DmxSolver.Solve(Contract(0.85), zones);

            Assert.Equal("4ch (DMX-4-5000-10A)", bill.Zones.Single().Decoder.Name);
        }

        [Fact]
        public void FiveChannelTape_WithOnlyFourChannelDecoders_AbortsWholeRun()
        {
            // The §6c hard-stop: contract misconfiguration ⇒ run-breaking, with an actionable message.
            var zones = new[] { new ZoneDesign("Lobby RGBTW", new[] { new TapeRun(40.0, 5.2, channels: 5) }) };
            var contract = Contract(0.85, decoders: new[] { DecoderSpec.Dmx4_5000_10A });

            var ex = Assert.Throws<UnmappableTapeException>(() => DmxSolver.Solve(contract, zones));
            Assert.Equal("Lobby RGBTW", ex.ZoneName);
            Assert.Equal(5, ex.ChannelsNeeded);
            Assert.Equal(4, ex.MaxOutputsAvailable);
        }

        // --- The §6a bracket: same 40×475 W RGBW tape, two zonings, opposite binding limits ---

        private static TapeRun[] FortyRuns() =>
            Enumerable.Range(0, 40).Select(_ => new TapeRun(475.0 / 5.2, 5.2, 4)).ToArray();

        [Fact]
        public void Bracket_AllOneZone_IsChannelCheap_ButDeviceBound()
        {
            var bill = DmxSolver.Solve(Contract(0.80), new[] { new ZoneDesign("ALL", FortyRuns()) });

            Assert.Equal(40, bill.TotalDecoders);
            Assert.Equal(4, bill.TotalChannels);
            Assert.Equal(1, bill.InterfaceCount);
            Assert.Equal(1, bill.TotalRepeaters);
            Assert.Equal(new[] { 20, 20 }, bill.Interfaces[0].Segmentation.Segments.Select(s => s.DeviceCount));
        }

        [Fact]
        public void Bracket_EachRunOwnZone_IsDeviceCheap_ButChannelBound()
        {
            var zones = FortyRuns().Select((r, i) => new ZoneDesign($"z{i}", new[] { r })).ToArray();
            var bill = DmxSolver.Solve(Contract(0.80), zones);

            Assert.Equal(40, bill.TotalDecoders);
            Assert.Equal(160, bill.TotalChannels);
            Assert.Equal(5, bill.InterfaceCount);
            Assert.Equal(0, bill.TotalRepeaters);
        }
    }
}
