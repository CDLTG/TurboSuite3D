using System.Linq;
using TurboSuite.Dmx;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Addressing &amp; mirroring. Stride/mirroring values are read off the reference one-lines (Tier B);
    /// the rest is contiguity arithmetic (Tier A). Zones now declare a channel count, not a color.
    /// </summary>
    public class AddresserTests
    {
        [Fact]
        public void FoyerCove_RgbThenWhite_AreContiguous_LikeScreenshot195()
        {
            // 195 shows 9-6 "cove-rgb" on ch 8/9/10 and 9-7 "cove-white" on ch 11.
            var zones = new[] { new ZoneInput("Foyer Cove", channels: 4, decoderCount: 3) };

            var addressed = Addresser.Assign(zones, startAddress: 8);

            var rgb = addressed[0].SubZones.Single(s => s.Role == ColorRole.Rgb);
            var white = addressed[0].SubZones.Single(s => s.Role == ColorRole.White);
            Assert.Equal(8, rgb.StartAddress);
            Assert.Equal(11, white.StartAddress);
        }

        [Fact]
        public void TunableWhiteZones_StrideByTwo_001_003_005()
        {
            var zones = Enumerable.Range(1, 3).Select(i => new ZoneInput($"tw{i}", channels: 2, decoderCount: 1)).ToArray();
            var addressed = Addresser.Assign(zones);

            var coolStarts = addressed.Select(z => z.SubZones.First(s => s.Role == ColorRole.Cool).StartAddress);
            Assert.Equal(new[] { 1, 3, 5 }, coolStarts);
        }

        [Fact]
        public void RgbwZones_StrideByFour_001_005_009()
        {
            var zones = Enumerable.Range(1, 3).Select(i => new ZoneInput($"z{i}", channels: 4, decoderCount: 1)).ToArray();
            var addressed = Addresser.Assign(zones);

            var rgbStarts = addressed.Select(z => z.SubZones.First(s => s.Role == ColorRole.Rgb).StartAddress);
            Assert.Equal(new[] { 1, 5, 9 }, rgbStarts);
        }

        [Fact]
        public void Mirroring_ChannelCostIsIndependentOfDecoderCount()
        {
            var big = Addresser.Assign(new[] { new ZoneInput("huge", 4, decoderCount: 20) });
            var small = Addresser.Assign(new[] { new ZoneInput("tiny", 4, decoderCount: 1) });

            Assert.Equal(4, big[0].ChannelsConsumed);
            Assert.Equal(big[0].ChannelsConsumed, small[0].ChannelsConsumed);
        }

        [Fact]
        public void TotalChannels_AreContiguousAndGapless()
        {
            var zones = new[]
            {
                new ZoneInput("a", 4, 2), // 4 ch: 1-4
                new ZoneInput("b", 1, 1), // 1 ch: 5
                new ZoneInput("c", 2, 1), // 2 ch: 6-7
            };

            var addressed = Addresser.Assign(zones);
            var occupied = addressed
                .SelectMany(z => z.SubZones)
                .SelectMany(s => Enumerable.Range(s.StartAddress, s.ChannelCount))
                .OrderBy(a => a).ToArray();

            Assert.Equal(Enumerable.Range(1, 7).ToArray(), occupied);
        }
    }
}
