using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for the room→location auto-seed classifier
    //  (Core/Zones/Services/RoomLocationSeeder.cs). Pure and Revit-free: given a room's two location
    //  signals (paneled-circuit panel names + DALI control-zone→loop votes), it classifies the room
    //  as unanimous (auto-seed), mixed (defer — don't guess), or none (no seed). The resolution ladder
    //  layers an explicit designer pick on top. This is Phase A of the located-keypads plan; a bug
    //  here mis-seeds a keypad's location on the control one-line.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    public class RoomLocationSeederTests
    {
        // ── Classification on the combined vote multiset ──────────────────────────────────────────

        [Fact]
        public void NoVotes_IsNone()
        {
            var seed = RoomLocationSeeder.Classify(Enumerable.Empty<int>());
            Assert.Equal(RoomLocationSeedKind.None, seed.Kind);
            Assert.Equal(0, seed.Location);
            Assert.Empty(seed.Candidates);
        }

        [Fact]
        public void SingleDistinctLocation_IsUnanimous()
        {
            // Several votes, all Location 1 → one distinct value → auto-seed 1.
            var seed = RoomLocationSeeder.Classify(new[] { 1, 1, 1 });
            Assert.Equal(RoomLocationSeedKind.Unanimous, seed.Kind);
            Assert.Equal(1, seed.Location);
            Assert.Equal(new[] { 1 }, seed.Candidates);
        }

        [Fact]
        public void MultipleDistinctLocations_IsMixed()
        {
            // Votes for 1 and 4 → genuine or collision disagreement → defer, flag.
            var seed = RoomLocationSeeder.Classify(new[] { 1, 4, 4 });
            Assert.Equal(RoomLocationSeedKind.Mixed, seed.Kind);
            Assert.Equal(0, seed.Location);
            Assert.Equal(new[] { 1, 4 }, seed.Candidates);           // distinct, ascending
            Assert.Equal(1, seed.Histogram[1]);                      // histogram hint
            Assert.Equal(2, seed.Histogram[4]);
        }

        [Fact]
        public void NonPositiveVotes_AreIgnored()
        {
            // 0 = unparseable/unassigned panel or not-yet-zoned DALI loop → contributes nothing.
            var seed = RoomLocationSeeder.Classify(new[] { 0, 0, 2, 0 });
            Assert.Equal(RoomLocationSeedKind.Unanimous, seed.Kind);
            Assert.Equal(2, seed.Location);
        }

        // ── The two signals ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void PanelVotes_ParseViaNamePrefix()
        {
            // "1-A"/"1-B" → 1, "ZONE 2" → 2 (legacy), "DUMMY" → 0 (dropped), "" → 0 (dropped).
            var votes = RoomLocationSeeder.PanelVotes(new[] { "1-A", "1-B", "DUMMY", "" }).ToList();
            Assert.Equal(new[] { 1, 1 }, votes);
        }

        [Fact]
        public void PaneledOnly_Seeds()
        {
            var seed = RoomLocationSeeder.Classify(
                RoomLocationSeeder.PanelVotes(new[] { "3-A", "3-C" }),
                RoomLocationSeeder.DaliVotes(Enumerable.Empty<string>(),
                    new List<(IReadOnlyList<string>, int)>()));
            Assert.Equal(RoomLocationSeedKind.Unanimous, seed.Kind);
            Assert.Equal(3, seed.Location);
        }

        [Fact]
        public void DaliOnly_Seeds_PanelLessRoom()
        {
            // A panel-less DALI room: its Control Zone "ZN-A" belongs to a loop assigned to ZONE 5.
            var loops = new List<(IReadOnlyList<string>, int)>
            {
                (new[] { "ZN-A", "ZN-B" }, 5),
                (new[] { "ZN-C" }, 7),
            };
            var daliVotes = RoomLocationSeeder.DaliVotes(new[] { "ZN-A" }, loops);
            var seed = RoomLocationSeeder.Classify(Enumerable.Empty<int>(), daliVotes);
            Assert.Equal(RoomLocationSeedKind.Unanimous, seed.Kind);
            Assert.Equal(5, seed.Location);
        }

        [Fact]
        public void DaliVote_NotYetZonedLoop_ContributesNothing()
        {
            // AssignedZone 0 = loop declared but not zoned in TurboDALI → graceful no-vote.
            var loops = new List<(IReadOnlyList<string>, int)> { (new[] { "ZN-A" }, 0) };
            Assert.Empty(RoomLocationSeeder.DaliVotes(new[] { "ZN-A" }, loops));
        }

        [Fact]
        public void BothSignalsAgree_Seeds()
        {
            var loops = new List<(IReadOnlyList<string>, int)> { (new[] { "ZN-A" }, 2) };
            var seed = RoomLocationSeeder.Classify(
                RoomLocationSeeder.PanelVotes(new[] { "2-A" }),
                RoomLocationSeeder.DaliVotes(new[] { "ZN-A" }, loops));
            Assert.Equal(RoomLocationSeedKind.Unanimous, seed.Kind);
            Assert.Equal(2, seed.Location);
        }

        [Fact]
        public void BothSignalsDisagree_IsMixed()
        {
            // Paneled says 2, DALI says 6 → mixed → defer (the designer resolves, usually a rename).
            var loops = new List<(IReadOnlyList<string>, int)> { (new[] { "ZN-A" }, 6) };
            var seed = RoomLocationSeeder.Classify(
                RoomLocationSeeder.PanelVotes(new[] { "2-A" }),
                RoomLocationSeeder.DaliVotes(new[] { "ZN-A" }, loops));
            Assert.Equal(RoomLocationSeedKind.Mixed, seed.Kind);
            Assert.Equal(new[] { 2, 6 }, seed.Candidates);
        }

        // ── Resolution ladder: explicit pick → auto-seed → blank ──────────────────────────────────

        [Fact]
        public void Resolve_UnanimousSeed_WhenNoPick()
        {
            var seed = RoomLocationSeeder.Classify(new[] { 4 });
            Assert.Equal(4, RoomLocationSeeder.Resolve(0, seed));
        }

        [Fact]
        public void Resolve_ExplicitPick_OverridesSeed()
        {
            // Auto-seed says 4, designer picked 9 → the pick wins (and never overwrites: seed stays).
            var seed = RoomLocationSeeder.Classify(new[] { 4 });
            Assert.Equal(9, RoomLocationSeeder.Resolve(9, seed));
        }

        [Fact]
        public void Resolve_ExplicitPick_WinsOverMixed()
        {
            // The manual pick is the backstop for a genuine mixed room.
            var seed = RoomLocationSeeder.Classify(new[] { 1, 4 });
            Assert.Equal(1, RoomLocationSeeder.Resolve(1, seed));
        }

        [Fact]
        public void Resolve_Mixed_NoPick_IsBlank()
        {
            var seed = RoomLocationSeeder.Classify(new[] { 1, 4 });
            Assert.Equal(0, RoomLocationSeeder.Resolve(0, seed));
        }

        [Fact]
        public void Resolve_None_NoPick_IsBlank()
        {
            var seed = RoomLocationSeeder.Classify(Enumerable.Empty<int>());
            Assert.Equal(0, RoomLocationSeeder.Resolve(0, seed));
        }
    }
}
