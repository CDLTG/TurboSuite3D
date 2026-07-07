using System;
using System.Linq;
using TurboSuite.Dmx;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Step 8 oracle — repeater/segment split on D4 (devices/segment). The all-one-zone case
    /// (53 decoders → 27/26) is the centerpiece. CONFIDENCE: Tier A arithmetic; D4 itself is a
    /// Tier-C input (~32 RS-485 default), handled as a parameter so there's no constant to confirm.
    /// </summary>
    public class LoopSegmenterTests
    {
        private const int D4 = 32; // the default device limit, supplied as input

        [Fact]
        public void UnderLimit_OneSegment_NoRepeater()
        {
            var seg = LoopSegmenter.Segment(deviceCount: 30, maxDevicesPerSegment: D4);

            Assert.Equal(1, seg.SegmentCount);
            Assert.Equal(0, seg.RepeaterCount);
        }

        [Fact]
        public void Section6a_53Decoders_SplitTo27And26_ZeroChannelCost()
        {
            // The all-one-zone extreme: 53 > 32 ⇒ one splitter, two balanced segments, still 1 address.
            var seg = LoopSegmenter.Segment(deviceCount: 53, maxDevicesPerSegment: D4);

            Assert.Equal(new[] { 27, 26 }, seg.Segments.Select(s => s.DeviceCount));
            Assert.Equal(1, seg.RepeaterCount);
            Assert.Equal(0, seg.ExtraChannelCost);
        }

        [Fact]
        public void SplitIsBalanced_NotFillAndSpill()
        {
            // : 53 must be 27/26, NOT 32/21. Balance ⇒ segment sizes differ by ≤ 1.
            var seg = LoopSegmenter.Segment(53, D4);
            int max = seg.Segments.Max(s => s.DeviceCount);
            int min = seg.Segments.Min(s => s.DeviceCount);

            Assert.True(max - min <= 1, $"unbalanced: {max} vs {min}");
            Assert.DoesNotContain(seg.Segments, s => s.DeviceCount == 32); // not filled to the cap
        }

        [Theory]
        [InlineData(32, 32, 1)]   // exactly at the cap ⇒ still one segment
        [InlineData(33, 32, 2)]   // one over ⇒ split (17/16)
        [InlineData(100, 32, 4)]  // 25/25/25/25
        [InlineData(64, 32, 2)]   // 32/32
        public void SegmentCount_IsCeilOfDevicesOverLimit(int devices, int limit, int expectedSegments)
        {
            var seg = LoopSegmenter.Segment(devices, limit);
            Assert.Equal(expectedSegments, seg.SegmentCount);
        }

        [Fact]
        public void D4_IsAnInput_RaisingItRemovesTheSplit()
        {
            // Modern ⅛-UL transceivers allow up to 256. At D4=256, 53 devices need no split.
            var seg = LoopSegmenter.Segment(deviceCount: 53, maxDevicesPerSegment: 256);
            Assert.Equal(1, seg.SegmentCount);
        }

        // --- Invariants over many shapes ---

        [Theory]
        [InlineData(1, 32)]
        [InlineData(53, 32)]
        [InlineData(211, 32)]
        [InlineData(500, 16)]
        public void EverySegmentWithinLimit_AndDevicesConserved(int devices, int limit)
        {
            var seg = LoopSegmenter.Segment(devices, limit);

            Assert.All(seg.Segments, s => Assert.True(s.DeviceCount <= limit));
            Assert.Equal(devices, seg.Segments.Sum(s => s.DeviceCount)); // none lost or invented
        }

        [Fact]
        public void ZeroDevices_NoSegments()
        {
            Assert.Empty(LoopSegmenter.Segment(0, D4).Segments);
        }

        [Fact]
        public void BadInputs_Throw()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => LoopSegmenter.Segment(10, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => LoopSegmenter.Segment(-1, 32));
        }
    }
}
