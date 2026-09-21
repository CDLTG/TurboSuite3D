using System.Collections.Generic;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for OrphanLocationService — the orphan/host bookkeeping behind the one user input
    //  in Section 1 (plan item 5). An orphan is a location with panels but no processor; a host is a
    //  processor-bearing location. The reconciler discards every stale assignment on rebuild.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    public class OrphanLocationServiceTests
    {
        private static PanelResult Plain(string name)
            => new PanelResult { PanelName = name, SelectedPanelSize = 8 };

        private static PanelResult WithProcessor(string name)
            => new PanelResult
            {
                PanelName = name,
                SelectedPanelSize = 8,
                SpecialCompartmentPanelSizes = new HashSet<int> { 8 },
                SelectedSpecialDevice = "Processor"
            };

        private static LocationResult Loc(int number, params PanelResult[] panels)
            => new LocationResult { LocationNumber = number, Panels = new List<PanelResult>(panels) };

        private static LocationResult ShadeOnlyLoc(int number)
            => new LocationResult
            {
                LocationNumber = number,
                ShadePanels = new List<ShadePanelResult>
                {
                    new ShadePanelResult { LocationNumber = number, PanelName = $"{number}-D", ShadeCount = 5 }
                }
            };

        private static PanelAllocationResult Allocation(params LocationResult[] locations)
            => new PanelAllocationResult { Locations = new List<LocationResult>(locations) };

        [Fact]
        public void HostsAreProcessorBearingLocations()
        {
            var alloc = Allocation(Loc(1, WithProcessor("1-A")), Loc(2, Plain("2-A")));

            Assert.Equal(new SortedSet<int> { 1 }, OrphanLocationService.HostLocations(alloc));
        }

        [Fact]
        public void OrphansHavePanelsButNoProcessor_ShadeOnlyIncluded()
        {
            var alloc = Allocation(
                Loc(1, WithProcessor("1-A")),   // host, not an orphan
                Loc(2, Plain("2-A")),           // panels, no processor → orphan
                ShadeOnlyLoc(3));               // shade panels only, no processor → orphan

            Assert.Equal(new SortedSet<int> { 2, 3 }, OrphanLocationService.OrphanLocations(alloc));
        }

        [Fact]
        public void LocationZeroIsNeverAnOrphanOrHost()
        {
            var alloc = Allocation(Loc(0, Plain("A")), Loc(0, WithProcessor("B")));

            Assert.Empty(OrphanLocationService.OrphanLocations(alloc));
            Assert.Empty(OrphanLocationService.HostLocations(alloc));
        }

        [Fact]
        public void ReconcileKeepsAValidAssignment()
        {
            var alloc = Allocation(Loc(1, WithProcessor("1-A")), Loc(3, Plain("3-A")));
            var map = new Dictionary<int, int> { { 3, 1 } };   // orphan 3 → host 1

            Assert.Equal(map, OrphanLocationService.Reconcile(map, alloc));
        }

        [Fact]
        public void ReconcileDropsWhenOrphanGetsItsOwnProcessor()
        {
            // Location 3 now has its own processor — no longer an orphan, so the assignment is stale.
            var alloc = Allocation(Loc(1, WithProcessor("1-A")), Loc(3, WithProcessor("3-A")));
            var map = new Dictionary<int, int> { { 3, 1 } };

            Assert.Empty(OrphanLocationService.Reconcile(map, alloc));
        }

        [Fact]
        public void ReconcileDropsWhenHostLosesItsProcessor()
        {
            // Location 1 lost its processor — no longer a host. (Location 1 may itself be an orphan now;
            // that is fine — no chains, since the dropped assignment does not re-point anywhere.)
            var alloc = Allocation(Loc(1, Plain("1-A")), Loc(3, Plain("3-A")));
            var map = new Dictionary<int, int> { { 3, 1 } };

            Assert.Empty(OrphanLocationService.Reconcile(map, alloc));
        }

        [Fact]
        public void ReconcileDropsARenamedAwayLocation()
        {
            // Location 3 no longer exists (renamed/removed) — its assignment cannot survive.
            var alloc = Allocation(Loc(1, WithProcessor("1-A")), Loc(2, Plain("2-A")));
            var map = new Dictionary<int, int> { { 3, 1 } };

            Assert.Empty(OrphanLocationService.Reconcile(map, alloc));
        }

        [Fact]
        public void ReconcileDropsASelfMap()
        {
            var alloc = Allocation(Loc(1, WithProcessor("1-A")), Loc(3, Plain("3-A")));
            var map = new Dictionary<int, int> { { 3, 3 } };   // a location cannot host itself

            Assert.Empty(OrphanLocationService.Reconcile(map, alloc));
        }
    }
}
