using TurboSuite.Shared.Services;
using Xunit;

namespace TurboSuite.Tests
{
    /// <summary>
    /// Oracle tests for the BAND_ROOM tiebreak (Core/Shared/Services/RoomBandSelector.cs): among the rooms a
    /// fixture matches in plan, pick the one whose reconstructed vertical band contains it — i.e. the highest
    /// floor at or below the fixture. Floor elevations are in host coordinates; a fixture below every floor
    /// falls back to the lowest. The plenum and mis-drawn-overlap cases below are real ground-truthed
    /// failures from the model this rule was derived on.
    /// </summary>
    public class RoomBandSelectorTests
    {
        [Fact]
        public void NoCandidates_ReturnsMinusOne()
            => Assert.Equal(-1, RoomBandSelector.SelectBandIndex(new double[0], 0.0));

        [Fact]
        public void SingleCandidate_AlwaysWins_EvenBelowItsFloor()
        {
            var floors = new[] { -1.50 };
            Assert.Equal(0, RoomBandSelector.SelectBandIndex(floors, 3.00));   // above the floor
            Assert.Equal(0, RoomBandSelector.SelectBandIndex(floors, -9.99));  // below the floor (fallback)
        }

        [Fact]
        public void StackedStoreys_PicksHighestFloorAtOrBelowFixture()
        {
            // Three storeys stacked in one plan column; fixture on the top storey.
            var floors = new[] { -23.61, -13.61, -1.50 };
            Assert.Equal(2, RoomBandSelector.SelectBandIndex(floors, 6.00));   // top storey
            Assert.Equal(1, RoomBandSelector.SelectBandIndex(floors, -8.00));  // middle storey
            Assert.Equal(0, RoomBandSelector.SelectBandIndex(floors, -20.00)); // bottom storey
        }

        [Fact]
        public void FixtureExactlyOnAFloor_CountsAsAtOrBelow_PicksThatFloor()
        {
            var floors = new[] { -13.61, -1.50 };
            // Fixture Z exactly on the upper floor: EPS admits it, so the upper room wins, not the lower.
            Assert.Equal(1, RoomBandSelector.SelectBandIndex(floors, -1.50));
        }

        [Fact]
        public void FixtureWithinEpsilonAboveAFloor_StillCountsAsAtOrBelow()
        {
            var floors = new[] { -13.61, -1.50 };
            // 0.005 ft below the upper floor — inside the 0.01 tolerance, so the upper room still wins.
            Assert.Equal(1, RoomBandSelector.SelectBandIndex(floors, -1.505));
        }

        [Fact]
        public void PlenumCase_HandsFixtureToLowerStorey_NotByNearestDistance()
        {
            // Canonical plenum case: fixture at -3.55 sits 2.05 ft *below* the upper floor (bedroom, -1.50) and
            // 2.06 ft above the lower room's arbitrary top. Nearest-distance would hand it upward by a
            // 0.01 ft margin; banding puts it below the slab it is physically beneath — the living room.
            var floors = new[] { -1.50, -13.61 }; // [bedroom above, living below]
            Assert.Equal(1, RoomBandSelector.SelectBandIndex(floors, -3.55));
        }

        [Fact]
        public void MisDrawnOverlap_PicksHigherFlooredLowerRoom_NotRealZContainment()
        {
            // Mis-drawn overlap: a wall LED sheet at Z -4.57 is really in the powder room (floor -13.61), whose real
            // ceiling is above -4.57 but whose architect Room top stops at -5.61. A mis-drawn double-height
            // hall/stair (floor -23.61) overlaps in plan and swallows -4.57 at real Z. Banding ignores both
            // fictions and takes the higher-floored lower room: powder, not hall/stair.
            var floors = new[] { -13.61, -23.61 }; // [powder, hall/stair]
            Assert.Equal(0, RoomBandSelector.SelectBandIndex(floors, -4.57));
        }

        [Fact]
        public void BelowEveryFloor_FallsBackToLowestFloor()
        {
            // Slab-recessed / exterior grade: fixture beneath all candidate floors → lowest floor wins.
            var floors = new[] { -1.50, -13.61, -23.61 };
            Assert.Equal(2, RoomBandSelector.SelectBandIndex(floors, -30.00));
        }

        [Fact]
        public void TieInFloorElevation_ResolvesToEarliestCandidate()
        {
            // Two candidates share a floor (host-doc room added first). Earliest wins for both the
            // at-or-below path and the below-all fallback path — preserving host-before-link ordering.
            var shared = new[] { -1.50, -1.50 };
            Assert.Equal(0, RoomBandSelector.SelectBandIndex(shared, 2.00));   // at-or-below path
            Assert.Equal(0, RoomBandSelector.SelectBandIndex(shared, -9.00));  // below-all fallback path
        }
    }
}
