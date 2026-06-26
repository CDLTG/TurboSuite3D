using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx.Input;
using TurboSuite.Dmx.Persistence;
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

        // ── Persistence (BuildPlan Phase 2: declarations survive reopen via the doc-side ES bundle) ────

        [Fact]
        public void DeclarationChangesFirePersistWithCurrentState()
        {
            DmxModuleState? captured = null;
            var vm = new DmxMainViewModel(Snapshot(new[] { Fix("Z1"), Fix("Z2") }), persist: s => captured = s);

            Assert.Null(captured); // initial load + Run must NOT write the model back to itself

            vm.ReservedChannels = 3;
            vm.DecoderRows.Single(r => r.Candidate.TypeId == "dec6").IsSelected = false;
            vm.AddLoopCommand.Execute(null);
            vm.Loops[0].Name = "House";
            vm.Loops[0].Zones.Single(z => z.ZoneName == "Z1").IsAssigned = true;

            Assert.NotNull(captured);
            Assert.Equal(3, captured.Settings.ReservedChannels);
            Assert.Equal(new[] { "dec4" }, captured.Settings.DecoderTypeIds); // dec6 unticked
            var loop = Assert.Single(captured.Loops);
            Assert.Equal("House", loop.Name);
            Assert.Equal(new[] { "Z1" }, loop.ZoneValues);
        }

        [Fact]
        public void SavedStateRestoresOnReopen()
        {
            var saved = new DmxModuleState
            {
                Settings = new DmxSettingsDto
                {
                    Profile = "Lutron",
                    ReservedChannels = 5,
                    MaxDevicesPerSegment = 16,
                    DecoderTypeIds = new List<string> { "dec6" }, // only the 6ch curated
                    DriverTypeIds = new List<string> { "md" },
                },
                Loops = new List<DmxLoopDto>
                {
                    new DmxLoopDto { Name = "L1", Order = 0, ZoneValues = new List<string> { "Z1", "Z2" } },
                },
            };

            var vm = new DmxMainViewModel(Snapshot(new[] { Fix("Z1"), Fix("Z2"), Fix("Z3") }), state: saved);

            Assert.Equal(5, vm.ReservedChannels);
            Assert.Equal(16, vm.MaxDevicesPerSegment);
            Assert.False(vm.DecoderRows.Single(r => r.Candidate.TypeId == "dec4").IsSelected);
            Assert.True(vm.DecoderRows.Single(r => r.Candidate.TypeId == "dec6").IsSelected);

            var loop = Assert.Single(vm.Loops);
            Assert.Equal("L1", loop.Name);
            Assert.Equal(new[] { "Z1", "Z2" }, loop.AssignedZoneNames);
        }

        [Fact]
        public void EmptyCuratedListRestoresToAllSelectedDefault()
        {
            // A never-curated save (empty TypeId lists) must reopen with the all-discovered default, not "none".
            var saved = new DmxModuleState { Settings = new DmxSettingsDto() };
            var vm = new DmxMainViewModel(Snapshot(new[] { Fix("Z1") }), state: saved);
            Assert.True(vm.DecoderRows.All(r => r.IsSelected));
            Assert.True(vm.DriverRows.All(r => r.IsSelected));
        }
    }
}
