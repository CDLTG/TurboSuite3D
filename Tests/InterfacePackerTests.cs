using System;
using System.Linq;
using TurboSuite.Dmx;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Step 7 oracle — packing zones into interfaces under the D1 budget (ceiling − reserved).
    /// The 26-vs-2 interface counts are the §6a / §1.6 extremes; reserved-channel subtraction is
    /// §3c. CONFIDENCE: Tier A arithmetic, with the 32/512 ceilings as Tier-B profile values.
    /// </summary>
    public class InterfacePackerTests
    {
        private const int Lutron = 32;
        private const int NativeUniverse = 512;

        private static ZoneInput[] RgbwZones(int n) =>
            Enumerable.Range(1, n).Select(i => new ZoneInput($"z{i}", channels: 4, decoderCount: 1)).ToArray();

        // --- The two §6a / §1.6 extremes: same 832 channels, different ceilings ---

        [Fact]
        public void EachRunOwnZone_832Channels_PacksTo26Interfaces_OnLutron()
        {
            // 208 RGBW zones × 4 ch = 832; 832 / 32 = 26 (D1-bound).
            var result = InterfacePacker.Pack(RgbwZones(208), Lutron);

            Assert.Equal(26, result.InterfaceCount);
            Assert.All(result.Interfaces, i => Assert.True(i.ChannelsUsed <= 32));
        }

        [Fact]
        public void SameLoad_OnNative512Profile_PacksTo2Interfaces()
        {
            // §1.6: a native 512-channel universe nearly removes D1 as a binding limit. 832 / 512 ⇒ 2.
            var result = InterfacePacker.Pack(RgbwZones(208), NativeUniverse);

            Assert.Equal(2, result.InterfaceCount);
        }

        // --- Reserved smart-fixture channels shrink the budget (§3c) ---

        [Fact]
        public void ReservedChannels_AreSubtractedFromBudget()
        {
            // Ceiling 32, reserve 5 (downlights) ⇒ budget 27 ⇒ 6 RGBW zones (24 ch) fit, a 7th (28) spills.
            var fits = InterfacePacker.Pack(RgbwZones(6), Lutron, reservedChannels: 5);
            var spills = InterfacePacker.Pack(RgbwZones(7), Lutron, reservedChannels: 5);

            Assert.Equal(1, fits.InterfaceCount);
            Assert.Equal(2, spills.InterfaceCount);
            Assert.Equal(27, fits.ChannelBudget);
        }

        [Fact]
        public void ReservedExceedingCeiling_Throws()
        {
            Assert.Throws<ArgumentException>(() => InterfacePacker.Pack(RgbwZones(1), Lutron, reservedChannels: 32));
        }

        // --- Per-interface universe: addresses restart at 1 ---

        [Fact]
        public void EachInterface_AddressesRestartAtSlotOne()
        {
            // 8 RGBW zones fill interface 1 (32 ch); the 9th opens interface 2 at address 1 again.
            var result = InterfacePacker.Pack(RgbwZones(9), Lutron);

            Assert.Equal(2, result.InterfaceCount);
            int firstAddrIf1 = result.Interfaces[0].Zones.First().SubZones.First().StartAddress;
            int firstAddrIf2 = result.Interfaces[1].Zones.First().SubZones.First().StartAddress;
            Assert.Equal(1, firstAddrIf1);
            Assert.Equal(1, firstAddrIf2); // not 33 — new universe
        }

        // --- Invariants: zones kept whole, nothing lost, every interface within ceiling ---

        [Fact]
        public void EveryZoneLandsOnExactlyOneInterface_AndBudgetIsRespected()
        {
            var zones = RgbwZones(50);
            var result = InterfacePacker.Pack(zones, Lutron, reservedChannels: 4);

            var placedNames = result.Interfaces.SelectMany(i => i.Zones).Select(z => z.ZoneName).ToArray();
            Assert.Equal(zones.Length, placedNames.Length);          // none dropped
            Assert.Equal(zones.Length, placedNames.Distinct().Count()); // none duplicated/split
            Assert.All(result.Interfaces, i => Assert.True(i.ChannelsUsed <= result.ChannelBudget));
        }
    }
}
