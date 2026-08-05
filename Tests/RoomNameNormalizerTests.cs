using TurboSuite.Name;
using Xunit;

namespace TurboSuite.Tests
{
    /// <summary>
    /// Oracle tests for the shared room-name rule (Core/Name/RoomNameNormalizer.cs): trim → strip '#' →
    /// uppercase. This is the single rule behind both TurboName's CAD extraction and the space-naming command,
    /// so these pin the behavior that keeps the two producers byte-identical.
    /// </summary>
    public class RoomNameNormalizerTests
    {
        [Theory]
        [InlineData("kitchen", "KITCHEN")]
        [InlineData("Powder", "POWDER")]
        [InlineData("great room", "GREAT ROOM")]
        public void Uppercases(string raw, string expected)
            => Assert.Equal(expected, RoomNameNormalizer.Normalize(raw));

        [Theory]
        [InlineData("  kitchen  ", "KITCHEN")]
        [InlineData("\tbath 2\r\n", "BATH 2")]
        public void TrimsEnds(string raw, string expected)
            => Assert.Equal(expected, RoomNameNormalizer.Normalize(raw));

        [Theory]
        [InlineData("powder #2", "POWDER 2")]   // '#' removed, surrounding text kept
        [InlineData("#garage", "GARAGE")]
        [InlineData("wc#", "WC")]
        public void StripsHash(string raw, string expected)
            => Assert.Equal(expected, RoomNameNormalizer.Normalize(raw));

        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        [InlineData("   ", "")]
        public void NullOrBlank_ReturnsEmpty(string raw, string expected)
            => Assert.Equal(expected, RoomNameNormalizer.Normalize(raw));

        [Fact]
        public void DoesNotCollapseInternalWhitespace()
            => Assert.Equal("A  B", RoomNameNormalizer.Normalize("a  b"));   // two spaces preserved
    }
}
