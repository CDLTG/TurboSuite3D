using System.Linq;
using TurboSuite.Dmx;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// The Link→Processor roll-up (Design §8b, Q8) — a pure REPORT pass (D2 is report-only, §6c). The
    /// engine sizes the DMX demand up the tiered ladder: interfaces → control links (≤512 switch legs /
    /// ≤99 devices each) → processors (≤2 links each, HQP7-2). It never provisions and never stops on it.
    /// </summary>
    public class LinkProcessorTests
    {
        private const double V = 24.0;

        [Theory]
        [InlineData(0, 2, 0)]
        [InlineData(1, 2, 1)]
        [InlineData(2, 2, 1)]
        [InlineData(3, 2, 2)]
        [InlineData(4, 2, 2)]
        [InlineData(5, 2, 3)]
        public void ProcessorCount_IsCeilLinksOverCapacity(int links, int per, int expected)
            => Assert.Equal(expected, LinkPacker.ProcessorCount(links, per)); // Tier A arithmetic

        [Fact]
        public void Links_BindOnLegs_WhenInterfacesAreFull()
        {
            // 16 full 32-ch interfaces = 512 legs ⇒ exactly one link; the 17th spills to a second.
            Assert.Single(LinkPacker.Pack(Enumerable.Repeat(32, 16).ToList(), 512, 99));
            Assert.Equal(2, LinkPacker.Pack(Enumerable.Repeat(32, 17).ToList(), 512, 99).Count);
        }

        [Fact]
        public void Links_BindOnDeviceCount_WhenInterfacesAreTiny()
        {
            // 100 one-channel interfaces: legs (100) are nowhere near 512, but the 99-device cap binds ⇒ 2.
            var links = LinkPacker.Pack(Enumerable.Repeat(1, 100).ToList(), 512, 99);
            Assert.Equal(2, links.Count);
            Assert.Equal(99, links[0].InterfaceCount);
            Assert.Equal(1, links[1].InterfaceCount);
        }

        [Fact]
        public void Links_RespectBothCaps_AndConserveChannels()
        {
            var channels = new[] { 32, 32, 8, 8, 1, 1, 1 };
            var links = LinkPacker.Pack(channels, 512, 99);

            Assert.All(links, l => Assert.True(l.ChannelsUsed <= 512));
            Assert.All(links, l => Assert.True(l.InterfaceCount <= 99));
            Assert.Equal(channels.Sum(), links.Sum(l => l.ChannelsUsed));      // conservation
            Assert.Equal(channels.Length, links.Sum(l => l.InterfaceCount));
        }

        // --- end-to-end through Solve --------------------------------------------------------------------

        private static DmxContract SmallLinkContract() => new DmxContract(
            decoderPool: new[] { DecoderSpec.Dmx4_5000_10A },
            driverPool: new[] { new DriverType("ME", 600, V, 0.85) },
            systemVolts: V, channelCeiling: 4, maxDevicesPerSegment: 32,
            linkChannelCapacity: 8, linkDeviceCapacity: 99, linksPerProcessor: 2);

        private static ZoneDesign Zone(string name) => new ZoneDesign(name, new[] { new TapeRun(5.0, 1.0, 4) });

        [Fact]
        public void Solve_RollsInterfacesUpToLinksAndProcessors()
        {
            // ceiling 4 ⇒ each 4-ch zone is its own interface. 5 interfaces, link cap 8 legs ⇒ 3 links
            // (4+4 / 4+4 / 4), 2 links/processor ⇒ 2 processors.
            var zones = Enumerable.Range(1, 5).Select(i => Zone($"Z{i}")).ToArray();

            var bill = DmxSolver.Solve(SmallLinkContract(), zones);

            Assert.Equal(5, bill.InterfaceCount);
            Assert.Equal(3, bill.RequiredLinks);
            Assert.Equal(2, bill.RequiredProcessors);
            Assert.Equal(20, bill.Links.Sum(l => l.ChannelsUsed)); // conservation: 5 × 4 ch
        }

        [Fact]
        public void Solve_SmallJob_IsOneLinkOneProcessor_OnLutronDefaults()
        {
            var contract = new DmxContract(
                decoderPool: new[] { DecoderSpec.Dmx4_5000_10A },
                driverPool: new[] { new DriverType("ME", 600, V, 0.85) },
                systemVolts: V, channelCeiling: 32, maxDevicesPerSegment: 32);
            // defaults: 512 legs / 99 devices / 2 links per processor

            var bill = DmxSolver.Solve(contract, new[] { Zone("A"), Zone("B") });

            Assert.Equal(1, bill.InterfaceCount); // 8 ch ≤ 32
            Assert.Equal(1, bill.RequiredLinks);
            Assert.Equal(1, bill.RequiredProcessors);
        }
    }
}
