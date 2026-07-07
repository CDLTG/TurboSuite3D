using System;
using System.Linq;
using TurboSuite.Dmx;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// The count → sub-zone structure convention (option a). A fixture declares only a channel COUNT;
    /// this table decomposes it into named Lutron primitives for addressing. Tier B for the
    /// decompositions (the reference one-line + the RGBATW cutsheet), Tier A for the sum invariant.
    /// </summary>
    public class SubZoneStructureTests
    {
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        public void SubZoneSizes_AlwaysSumToTheChannelCount(int channels)
        {
            Assert.Equal(channels, SubZoneStructure.For(channels).Sum(s => s.ChannelCount));
        }

        [Fact]
        public void Rgbw_SplitsIntoRgbThenWhite()
        {
            var subs = SubZoneStructure.For(4);
            Assert.Equal(new[] { ColorRole.Rgb, ColorRole.White }, subs.Select(s => s.Role));
            Assert.Equal(new[] { 3, 1 }, subs.Select(s => s.ChannelCount));
        }

        [Fact]
        public void Rgbtw_FiveChannels_SplitsIntoRgbCoolWarm()
        {
            var subs = SubZoneStructure.For(5);
            Assert.Equal(new[] { ColorRole.Rgb, ColorRole.Cool, ColorRole.Warm }, subs.Select(s => s.Role));
            Assert.Equal(new[] { 3, 1, 1 }, subs.Select(s => s.ChannelCount));
        }

        [Fact]
        public void SixInOne_RgbatwSplitsIntoRgbAmberCoolWarm()
        {
            // RGBATW 6-in-1 (cutsheet): rgb object + amber single + tunable-white (cool+warm).
            var subs = SubZoneStructure.For(6);
            Assert.Equal(new[] { ColorRole.Rgb, ColorRole.Amber, ColorRole.Cool, ColorRole.Warm }, subs.Select(s => s.Role));
        }

        [Fact]
        public void UndefinedChannelCount_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SubZoneStructure.For(7));
        }
    }
}
