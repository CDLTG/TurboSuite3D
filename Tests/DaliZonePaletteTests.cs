using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dali.Overlay;
using Xunit;

namespace TurboSuite.Tests.Dali
{
    /// <summary>
    /// Oracles for <see cref="DaliZonePalette"/> — the DALI Control-Zone overlay's pure palette (a copy of
    /// <see cref="TurboSuite.Tests.Dmx.DmxZonePaletteTests"/>). Locks the properties that matter for a
    /// distinguishability aid: deterministic + stable across opens, distinct colors per zone, blanks/dupes/
    /// case/whitespace folded out, assignment keyed off SORTED position.
    /// </summary>
    public class DaliZonePaletteTests
    {
        [Fact]
        public void Build_IsDeterministic_SameInputSameColors()
        {
            var zones = new[] { "Kitchen", "Bar", "Foyer" };
            var a = DaliZonePalette.Build(zones);
            var b = DaliZonePalette.Build(zones);
            foreach (var z in zones)
                Assert.Equal((a[z].R, a[z].G, a[z].B), (b[z].R, b[z].G, b[z].B));
        }

        [Fact]
        public void Build_KeyedOffSortedPosition_OrderIndependent()
        {
            var forward = DaliZonePalette.Build(new[] { "Bar", "Kitchen", "Foyer" });
            var shuffled = DaliZonePalette.Build(new[] { "Foyer", "Bar", "Kitchen" });
            foreach (var z in new[] { "Bar", "Kitchen", "Foyer" })
                Assert.Equal((forward[z].R, forward[z].G, forward[z].B),
                             (shuffled[z].R, shuffled[z].G, shuffled[z].B));
        }

        [Fact]
        public void Build_DistinctColorsPerZone()
        {
            var map = DaliZonePalette.Build(new[] { "A", "B", "C", "D", "E", "F" });
            var colors = map.Values.Select(c => (c.R, c.G, c.B)).ToHashSet();
            Assert.Equal(map.Count, colors.Count);
        }

        [Fact]
        public void Build_FoldsBlanksDuplicatesCaseAndWhitespace()
        {
            var map = DaliZonePalette.Build(new[] { "Kitchen", "kitchen", " Kitchen ", "", "  ", null });
            Assert.Single(map);
            Assert.True(map.ContainsKey("Kitchen"));
        }

        [Fact]
        public void Build_EmptyOrNull_ReturnsEmpty()
        {
            Assert.Empty(DaliZonePalette.Build(null));
            Assert.Empty(DaliZonePalette.Build(new List<string>()));
        }
    }
}
