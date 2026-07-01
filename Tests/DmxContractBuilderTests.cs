using TurboSuite.Dmx;
using TurboSuite.Dmx.Input;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>Oracles for <see cref="DmxProfile"/> defaults and <see cref="DmxContractBuilder"/> — the
    /// declarations → engine-contract assembly (Phase 1).</summary>
    public class DmxContractBuilderTests
    {
        [Fact]
        public void LutronProfileHas32ChannelCeiling()
        {
            Assert.Equal(32, DmxProfile.Lutron.ChannelCeiling);
            Assert.Equal(512, DmxProfile.Lutron.LinkChannelCapacity);
            Assert.Equal(99, DmxProfile.Lutron.LinkDeviceCapacity);
            Assert.Equal(2, DmxProfile.Lutron.LinksPerProcessor);
        }

        [Fact]
        public void NativeProfilesHave512ChannelCeiling()
        {
            Assert.Equal(512, DmxProfile.Crestron.ChannelCeiling);
            Assert.Equal(512, DmxProfile.Generic.ChannelCeiling);
        }

        [Fact]
        public void ByNameIsCaseInsensitiveAndFallsBackToLutron()
        {
            Assert.Same(DmxProfile.Crestron, DmxProfile.ByName("crestron"));
            Assert.Same(DmxProfile.Lutron, DmxProfile.ByName("nonsense"));
            Assert.Same(DmxProfile.Lutron, DmxProfile.ByName(null));
        }

        [Fact]
        public void ProfileSuppliesCeilingAndLinkCapsToContract()
        {
            var contract = DmxContractBuilder.Build(
                DmxProfile.Lutron, new DmxJobSettings(),
                new[] { new DmxDecoderCandidate { Name = "4ch", MaxOutputs = 4, MaxAmpsPerOutput = 10, MaxWatts = 960 } },
                new[] { new DmxDriverCandidate { Name = "MD", RatedWatts = 288, OperatingVolts = 24, DeratingFactorRaw = 0.8 } });

            Assert.Equal(32, contract.ChannelCeiling);
            Assert.Equal(512, contract.LinkChannelCapacity);
            Assert.Single(contract.DecoderPool);
            Assert.Single(contract.DriverPool);
        }

        [Fact]
        public void JobSettingsOverrideContractPolicy()
        {
            var settings = new DmxJobSettings
            {
                SystemVolts = 48, BreakerAmps = 15, BreakerBasis = BreakerBasis.DriverRating
            };
            var contract = DmxContractBuilder.Build(
                DmxProfile.Lutron, settings,
                new[] { new DmxDecoderCandidate { Name = "4ch", MaxOutputs = 4, MaxAmpsPerOutput = 10, MaxWatts = 960 } },
                new[] { new DmxDriverCandidate { Name = "MD", RatedWatts = 288, OperatingVolts = 48, DeratingFactorRaw = 0.8 } });

            Assert.Equal(48, contract.SystemVolts);
            Assert.Equal(15, contract.BreakerAmps);
            Assert.Equal(LoopSegmenter.DevicesPerSegment, contract.MaxDevicesPerSegment); // fed by the Core const, not a setting
            Assert.Equal(BreakerBasis.DriverRating, contract.BreakerBasis);
        }
    }
}
