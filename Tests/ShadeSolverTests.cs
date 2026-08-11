using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for ShadeSolver (Core/Zones/Services/ShadeSolver.cs).
    //
    //  Shades are the second control subsystem after DMX, and — like the lighting panels — the QSPS-10PNL
    //  count is a RECOMMENDATION read off the circuits, never placed hardware. The two edges worth
    //  pinning: panels are recommended PER LOCATION and summed (ceil(33/10)+ceil(4/10) = 5, not
    //  ceil(37/10) = 4), and the link budget is devices = shades + recommended panels, legs = shades,
    //  because each recommended QSPS-10PNL is itself a QS device on top of its shades.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    public class ShadeSolverTests
    {
        private static ShadeLocationTally Loc(int shades, string name = "SHADE 1")
            => new ShadeLocationTally(name, shades);

        private static ControlSubsystemDemand Solve(params ShadeLocationTally[] locations)
            => ShadeSolver.Solve(locations.ToList());

        [Fact]
        public void NoLocations_IsACleanNothing()
        {
            var d = ShadeSolver.Solve(new List<ShadeLocationTally>());
            Assert.Empty(d.Parts);
            Assert.Equal(0, d.LinkDevices);
            Assert.Equal(0, d.LinkLoads);
            Assert.False(d.HasDiagnostic);
        }

        [Fact]
        public void NullLocations_DoesNotThrow()
            => Assert.Empty(ShadeSolver.Solve(null).Parts);

        [Fact]
        public void LocationsWithNoShades_IsACleanNothing()
            => Assert.Empty(Solve(Loc(0, "SHADE 1"), Loc(0, "SHADE 2")).Parts);

        /// <summary>Ten shades in one location is exactly one panel: 10 legs, 11 devices (the ten shades
        /// plus the panel itself).</summary>
        [Fact]
        public void FullLocation_IsOnePanel_ElevenDevicesTenLegs()
        {
            var d = Solve(Loc(10));

            var part = Assert.Single(d.Parts);
            Assert.Equal("QSPS-10PNL", part.PartNumber);
            Assert.Equal(1, part.Quantity);
            Assert.Equal(DemandMount.External, part.Mount);   // ordered, competes for no compartment
            Assert.Equal(11, d.LinkDevices);
            Assert.Equal(10, d.LinkLoads);
        }

        /// <summary>Eleven shades in one location ceil to two panels — the panel count is derived, not
        /// placed. Devices = 11 shades + 2 panels = 13; legs = 11.</summary>
        [Fact]
        public void OverTenInOneLocation_RecommendsTwoPanels()
        {
            var d = Solve(Loc(11));

            Assert.Equal(2, Assert.Single(d.Parts).Quantity);
            Assert.Equal(13, d.LinkDevices);
            Assert.Equal(11, d.LinkLoads);
        }

        /// <summary>The headline case: 33 shades in SHADE 1 and 4 in SHADE 2 recommend 4 + 1 = 5 panels.
        /// Devices = 37 shades + 5 panels = 42; legs = 37.</summary>
        [Fact]
        public void TwoLocations_RecommendPerLocationThenSum()
        {
            var d = Solve(Loc(33, "SHADE 1"), Loc(4, "SHADE 2"));

            Assert.Equal(5, Assert.Single(d.Parts).Quantity);
            Assert.Equal(42, d.LinkDevices);   // 37 shades + 5 panels
            Assert.Equal(37, d.LinkLoads);
        }

        /// <summary>Per-location ceil is not ceil-of-total: two locations of 6 need two panels (one each),
        /// where ceil(12/10) would wrongly say two only by coincidence — make the point with 6 + 6 vs a
        /// single 12, which the previous test's 11 already separates. Here 5 + 5 = two panels, not one.</summary>
        [Fact]
        public void PerLocationCeil_NotCeilOfTotal()
        {
            var perLocation = Solve(Loc(5, "SHADE 1"), Loc(5, "SHADE 2"));
            Assert.Equal(2, Assert.Single(perLocation.Parts).Quantity);   // 1 + 1

            var lumped = Solve(Loc(10, "SHADE 1"));
            Assert.Equal(1, Assert.Single(lumped.Parts).Quantity);        // the same ten, one location
        }

        /// <summary>A lone shade in its own location is a whole panel — the cost the designer's
        /// split-by-proximity trades on. 1 shade → 1 panel → 2 devices, 1 leg.</summary>
        [Fact]
        public void LoneShadeInItsOwnLocation_IsAWholePanel()
        {
            var d = Solve(Loc(20, "SHADE 1"), Loc(1, "SHADE 2"));

            Assert.Equal(3, Assert.Single(d.Parts).Quantity);   // 2 + 1
            Assert.Equal(24, d.LinkDevices);                    // 21 shades + 3 panels
            Assert.Equal(21, d.LinkLoads);
        }
    }
}
