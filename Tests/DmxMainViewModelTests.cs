using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx.Input;
using TurboSuite.Dmx.ViewModels;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Oracles for <see cref="DmxMainViewModel"/> — the window's declarations→bill behavior, exercised
    /// without Revit (the solve is pure, the work queue/reader are optional). Covers the empty-state
    /// guidance, a clean solve, loop declaration changing the interface count, and the over-ceiling gate.
    /// </summary>
    public class DmxMainViewModelTests
    {
        private static DmxModelSnapshot Snapshot(IEnumerable<DmxFixtureReading> fixtures) => new DmxModelSnapshot
        {
            Fixtures = fixtures.ToList(),
            DecoderCandidates = new[]
            {
                new DmxDecoderCandidate { TypeId = "dec4", Name = "4ch", MaxOutputs = 4, MaxAmpsPerOutput = 10, MaxWatts = 960 },
                new DmxDecoderCandidate { TypeId = "dec6", Name = "6ch", MaxOutputs = 6, MaxAmpsPerOutput = 6, MaxWatts = 864 },
            },
            DriverCandidates = new[]
            {
                new DmxDriverCandidate { TypeId = "md", Name = "MD", RatedWatts = 288, OperatingVolts = 24, DeratingFactorRaw = 0.8 },
            },
        };

        private static DmxFixtureReading Fix(string zone, int ch = 4, double len = 10) =>
            new DmxFixtureReading { ControlZone = zone, Channels = ch, LengthFt = len, WattsPerFt = 5.2 };

        [Fact]
        public void EmptyModelShowsGuidanceNotAResult()
        {
            var vm = new DmxMainViewModel(Snapshot(new DmxFixtureReading[0]));
            Assert.False(vm.Bill.HasResult);
            Assert.False(vm.Bill.IsError);
            Assert.Contains("No DMX fixtures", vm.Bill.StatusMessage);
        }

        [Fact]
        public void CleanModelSolvesOnConstructionWithDefaultAllSelected()
        {
            var vm = new DmxMainViewModel(Snapshot(new[] { Fix("Z1"), Fix("Z2"), Fix("Z3") }));
            Assert.True(vm.Bill.HasResult);
            Assert.Equal(3, vm.ZoneCount);
            Assert.Equal(3, vm.FixtureCount);
            Assert.True(vm.Bill.Decoders >= 3);
            Assert.True(vm.DecoderRows.All(r => r.IsSelected)); // default kit = all discovered
        }

        [Fact]
        public void NoDecoderTickedYieldsGuidance()
        {
            var vm = new DmxMainViewModel(Snapshot(new[] { Fix("Z1") }));
            foreach (var r in vm.DecoderRows) r.IsSelected = false;
            vm.Run();
            Assert.False(vm.Bill.HasResult);
            Assert.Contains("decoder", vm.Bill.StatusMessage);
        }

        [Fact]
        public void DeclaringLoopsSplitsZonesAcrossMoreInterfaces()
        {
            // 4 zones × 4ch = 16ch — auto-packs into ONE interface (≤ 32 ceiling).
            var vm = new DmxMainViewModel(Snapshot(new[] { Fix("Z1"), Fix("Z2"), Fix("Z3"), Fix("Z4") }));
            int autoInterfaces = vm.Bill.InterfaceCount;
            Assert.Equal(1, autoInterfaces);

            // Force a 2-loop split → two interfaces.
            vm.AddLoopCommand.Execute(null);
            vm.AddLoopCommand.Execute(null);
            vm.Loops[0].Zones.Single(z => z.ZoneName == "Z1").IsAssigned = true;
            vm.Loops[0].Zones.Single(z => z.ZoneName == "Z2").IsAssigned = true;
            vm.Loops[1].Zones.Single(z => z.ZoneName == "Z3").IsAssigned = true;
            vm.Loops[1].Zones.Single(z => z.ZoneName == "Z4").IsAssigned = true;
            vm.Run();

            Assert.Equal(2, vm.Bill.InterfaceCount);
        }

        [Fact]
        public void LoopOverInterfaceCeilingSurfacesGateError()
        {
            // 9 zones × 4ch = 36ch in one declared loop > 32 ceiling ⇒ OverCapLoops gate.
            var fixtures = Enumerable.Range(1, 9).Select(i => Fix($"Z{i}"));
            var vm = new DmxMainViewModel(Snapshot(fixtures));

            vm.AddLoopCommand.Execute(null);
            foreach (var z in vm.Loops[0].Zones) z.IsAssigned = true;
            vm.Run();

            Assert.True(vm.Bill.IsError);
            Assert.False(vm.Bill.HasResult);
        }
    }
}
