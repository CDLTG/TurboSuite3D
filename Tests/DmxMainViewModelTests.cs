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

        // The kit defaults to NOTHING ticked, so a solve needs an explicit kit — tick the whole discovered pool.
        private static DmxMainViewModel SolvableVm(IEnumerable<DmxFixtureReading> fixtures)
        {
            var vm = new DmxMainViewModel(Snapshot(fixtures));
            foreach (var r in vm.DecoderRows) r.IsSelected = true;
            foreach (var r in vm.DriverRows) r.IsSelected = true;
            return vm;
        }

        [Fact]
        public void EmptyModelShowsGuidanceNotAResult()
        {
            var vm = new DmxMainViewModel(Snapshot(new DmxFixtureReading[0]));
            Assert.False(vm.Bill.HasResult);
            Assert.False(vm.Bill.IsError);
            Assert.Contains("No DMX fixtures", vm.Bill.StatusMessage);
        }

        [Fact]
        public void FreshModelStartsWithNoKitTickedAndNoResult()
        {
            var vm = new DmxMainViewModel(Snapshot(new[] { Fix("Z1"), Fix("Z2"), Fix("Z3") }));
            Assert.Equal(3, vm.ZoneCount);
            Assert.Equal(3, vm.FixtureCount);
            Assert.False(vm.Bill.HasResult);                    // nothing ticked ⇒ guidance, not a bill
            Assert.All(vm.DecoderRows, r => Assert.False(r.IsSelected));
            Assert.All(vm.DriverRows, r => Assert.False(r.IsSelected));
        }

        [Fact]
        public void TickingTheKitProducesAResult()
        {
            var vm = SolvableVm(new[] { Fix("Z1"), Fix("Z2"), Fix("Z3") });
            Assert.True(vm.Bill.HasResult);
            Assert.True(vm.Bill.Decoders >= 3);
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

        // Select the named pool zones and pull them into a new loop (the "+ New loop" gesture, seeded from selection).
        private static DmxLoopRowViewModel NewLoop(DmxMainViewModel vm, params string[] zones)
        {
            foreach (var z in zones) vm.ZonePool.Single(p => p.ZoneName == z).IsSelected = true;
            vm.NewLoopCommand.Execute(null);
            return vm.Loops[vm.Loops.Count - 1];
        }

        [Fact]
        public void AllZonesStartInThePoolWithNoLoops()
        {
            var vm = new DmxMainViewModel(Snapshot(new[] { Fix("Z1"), Fix("Z2"), Fix("Z3") }));
            Assert.Equal(3, vm.ZonePool.Count);
            Assert.Empty(vm.Loops);
        }

        [Fact]
        public void DeclaringLoopsSplitsZonesAcrossMoreInterfaces()
        {
            // 4 zones × 4ch = 16ch — auto-packs into ONE interface (≤ 32 ceiling).
            var vm = SolvableVm(new[] { Fix("Z1"), Fix("Z2"), Fix("Z3"), Fix("Z4") });
            Assert.Equal(1, vm.Bill.InterfaceCount);

            // Force a 2-loop split by pulling zones from the pool → two interfaces.
            NewLoop(vm, "Z1", "Z2");
            NewLoop(vm, "Z3", "Z4");

            Assert.Equal(2, vm.Bill.InterfaceCount);
            Assert.Empty(vm.ZonePool);   // every zone now assigned
        }

        [Fact]
        public void AssigningAZoneToALoopRemovesItFromThePool_ReturningRestoresIt()
        {
            var vm = new DmxMainViewModel(Snapshot(new[] { Fix("Z1"), Fix("Z2") }));
            var loop = NewLoop(vm, "Z1");

            Assert.DoesNotContain(vm.ZonePool, p => p.ZoneName == "Z1");
            Assert.Contains(vm.ZonePool, p => p.ZoneName == "Z2");
            Assert.Equal(new[] { "Z1" }, loop.AssignedZoneNames);

            // ← return Z1 to the pool.
            loop.Zones.Single(z => z.ZoneName == "Z1").RemoveFromLoopCommand!.Execute(null);
            Assert.Contains(vm.ZonePool, p => p.ZoneName == "Z1");
            Assert.Empty(loop.Zones);
        }

        [Fact]
        public void LoopOverInterfaceCeilingSurfacesGateError()
        {
            // 9 zones × 4ch = 36ch in one declared loop > 32 ceiling ⇒ OverCapLoops gate.
            var fixtures = Enumerable.Range(1, 9).Select(i => Fix($"Z{i}"));
            var vm = SolvableVm(fixtures);

            NewLoop(vm, vm.ZonePool.Select(p => p.ZoneName).ToArray());   // all nine into one loop

            Assert.True(vm.Bill.IsError);
            Assert.False(vm.Bill.HasResult);
        }

        // ── Per-loop interface number (loop-centric): resolved from the last solve, gates Place/one-line ──

        [Fact]
        public void EmptyLoopHasNoInterfaceNumber()
        {
            var vm = new DmxMainViewModel(Snapshot(new[] { Fix("Z1") }));
            vm.NewLoopCommand.Execute(null);   // no pool selection ⇒ empty loop
            var loop = vm.Loops.Single();
            Assert.Equal(0, loop.InterfaceNumber);   // no zones ⇒ not in any solved interface
        }

        [Fact]
        public void AutoNamedLoopsNeverCollide()
        {
            var vm = new DmxMainViewModel(Snapshot(new[] { Fix("Z1") }));
            vm.NewLoopCommand.Execute(null);         // Loop 1
            vm.Loops[0].Name = "Loop 2";             // rename onto the next auto-name
            vm.NewLoopCommand.Execute(null);          // must skip "Loop 2"
            Assert.Equal(2, vm.Loops.Select(l => l.Name).Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void ManuallyRenamingOntoAnotherLoopAutoSuffixes()
        {
            var vm = new DmxMainViewModel(Snapshot(new[] { Fix("Z1") }));
            vm.NewLoopCommand.Execute(null);   // Loop 1
            vm.NewLoopCommand.Execute(null);   // Loop 2
            vm.Loops[1].Name = "Loop 1";        // collide with the first loop
            Assert.Equal("Loop 1 (2)", vm.Loops[1].Name);
            Assert.Equal(2, vm.Loops.Select(l => l.Name).Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void SolvedLoopResolvesToAnInterfaceNumber()
        {
            var vm = SolvableVm(new[] { Fix("Z1"), Fix("Z2") });
            var loop = NewLoop(vm, "Z1");
            Assert.True(loop.InterfaceNumber > 0);   // resolved to an interface in the last solve
        }

        // ── Persistence (declarations survive reopen via the doc-side ES bundle) ────

        [Fact]
        public void DeclarationChangesFirePersistWithCurrentState()
        {
            DmxModuleState? captured = null;
            var vm = new DmxMainViewModel(Snapshot(new[] { Fix("Z1"), Fix("Z2") }), persist: s => captured = s);

            Assert.Null(captured); // initial load + Run must NOT write the model back to itself

            vm.DecoderRows.Single(r => r.Candidate.TypeId == "dec4").IsSelected = true;
            vm.ZonePool.Single(p => p.ZoneName == "Z1").IsSelected = true;
            vm.NewLoopCommand.Execute(null);
            vm.Loops[0].Name = "House";
            vm.Loops[0].ReservedChannels = 3;

            Assert.NotNull(captured);
            Assert.Equal(new[] { "dec4" }, captured.Settings.DecoderTypeIds); // only dec4 ticked
            var loop = Assert.Single(captured.Loops);
            Assert.Equal("House", loop.Name);
            Assert.Equal(3, loop.ReservedChannels);
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
                    DecoderTypeIds = new List<string> { "dec6" }, // only the 6ch curated
                    DriverTypeIds = new List<string> { "md" },
                },
                Loops = new List<DmxLoopDto>
                {
                    new DmxLoopDto { Name = "L1", Order = 0, ReservedChannels = 5, ZoneValues = new List<string> { "Z1", "Z2" } },
                },
            };

            var vm = new DmxMainViewModel(Snapshot(new[] { Fix("Z1"), Fix("Z2"), Fix("Z3") }), state: saved);

            Assert.False(vm.DecoderRows.Single(r => r.Candidate.TypeId == "dec4").IsSelected);
            Assert.True(vm.DecoderRows.Single(r => r.Candidate.TypeId == "dec6").IsSelected);

            var loop = Assert.Single(vm.Loops);
            Assert.Equal("L1", loop.Name);
            Assert.Equal(5, loop.ReservedChannels);
            Assert.Equal(new[] { "Z1", "Z2" }, loop.AssignedZoneNames);
        }

        [Fact]
        public void EmptyCuratedListRestoresToNoneSelectedDefault()
        {
            // A never-curated save (empty TypeId lists) reopens with NOTHING ticked — the designer must
            // pick this job's kit before a bill solves.
            var saved = new DmxModuleState { Settings = new DmxSettingsDto() };
            var vm = new DmxMainViewModel(Snapshot(new[] { Fix("Z1") }), state: saved);
            Assert.All(vm.DecoderRows, r => Assert.False(r.IsSelected));
            Assert.All(vm.DriverRows, r => Assert.False(r.IsSelected));
        }
    }
}
