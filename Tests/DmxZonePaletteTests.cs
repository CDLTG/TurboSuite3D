using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx.Overlay;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Oracles for <see cref="DmxZonePalette"/> — the Phase 5 Control-Zone color overlay's pure palette. The
    /// colors only need to DIFFERENTIATE zones, so these lock the properties that matter: deterministic +
    /// stable across opens (same input ⇒ same colors), distinct colors per distinct zone, blanks/dupes/
    /// case/whitespace folded out, and the assignment keyed off SORTED position (so order-of-discovery
    /// doesn't reshuffle the palette).
    /// </summary>
    public class DmxZonePaletteTests
    {
        [Fact]
        public void Build_IsDeterministic_SameInputSameColors()
        {
            var zones = new[] { "Lobby", "Bar", "Pool" };
            var a = DmxZonePalette.Build(zones);
            var b = DmxZonePalette.Build(zones);
            foreach (var z in zones)
                Assert.Equal((a[z].R, a[z].G, a[z].B), (b[z].R, b[z].G, b[z].B));
        }

        [Fact]
        public void Build_KeyedOffSortedPosition_OrderIndependent()
        {
            var forward = DmxZonePalette.Build(new[] { "Bar", "Lobby", "Pool" });
            var shuffled = DmxZonePalette.Build(new[] { "Pool", "Bar", "Lobby" });
            foreach (var z in new[] { "Bar", "Lobby", "Pool" })
                Assert.Equal((forward[z].R, forward[z].G, forward[z].B),
                             (shuffled[z].R, shuffled[z].G, shuffled[z].B));
        }

        [Fact]
        public void Build_DistinctColorsPerZone()
        {
            var map = DmxZonePalette.Build(new[] { "A", "B", "C", "D", "E", "F" });
            var colors = map.Values.Select(c => (c.R, c.G, c.B)).ToHashSet();
            Assert.Equal(map.Count, colors.Count);
        }

        [Fact]
        public void Build_FoldsBlanksDuplicatesCaseAndWhitespace()
        {
            var map = DmxZonePalette.Build(new[] { "Lobby", "lobby", " Lobby ", "", "  ", null });
            Assert.Single(map);
            Assert.True(map.ContainsKey("Lobby"));
        }

        [Fact]
        public void Build_EmptyOrNull_ReturnsEmpty()
        {
            Assert.Empty(DmxZonePalette.Build(null));
            Assert.Empty(DmxZonePalette.Build(new List<string>()));
        }
    }
}
