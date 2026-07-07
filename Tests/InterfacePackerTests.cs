using System;
using System.Linq;
using TurboSuite.Dmx;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Step 7 oracle — packing zones into interfaces under the D1 budget. Auto-packed interfaces get the
    /// full ceiling; a declared loop's budget is ceiling − its OWN reserved (per-loop). The 26-vs-2
    /// interface counts are the extremes. CONFIDENCE: Tier A arithmetic, with the 32/512
    /// ceilings as Tier-B profile values.
    /// </summary>
    public class InterfacePackerTests
    {
        private const int Lutron = 32;
        private const int NativeUniverse = 512;

        private static ZoneInput[] RgbwZones(int n) =>
            Enumerable.Range(1, n).Select(i => new ZoneInput($"z{i}", channels: 4, decoderCount: 1)).ToArray();

        // A declared loop grouping the given zones onto one interface, reserving some channels off its budget.
        private static LoopDeclaration Loop(ZoneInput[] zones, int reserved) =>
            new LoopDeclaration("L", zones.Select(z => z.ZoneName).ToList(), reserved);

        // --- The two extremes: same 832 channels, different ceilings ---

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
            // : a native 512-channel universe nearly removes D1 as a binding limit. 832 / 512 ⇒ 2.
            var result = InterfacePacker.Pack(RgbwZones(208), NativeUniverse);

            Assert.Equal(2, result.InterfaceCount);
        }

        // --- A declared loop's own reserved channels shrink ITS budget (per-loop) ---

        [Fact]
        public void LoopReservedChannels_ShrinkThatLoopsBudget()
        {
            // Ceiling 32, the loop reserves 5 (downlights) ⇒ budget 27. 6 RGBW zones (24 ch) fit one interface,
            // which carries the reservation; the zones as a group don't spill — a loop is exactly one interface.
            var zones = RgbwZones(6);
            var fits = InterfacePacker.Pack(zones, Lutron, new[] { Loop(zones, reserved: 5) });

            Assert.Equal(1, fits.InterfaceCount);
            Assert.Equal(5, fits.Interfaces[0].ReservedChannels);
        }

        [Fact]
        public void LoopOverItsReservedBudget_Throws()
        {
            // 7 RGBW zones = 28 ch, but the loop reserving 5 leaves only 27 ⇒ over budget, the gate.
            var zones = RgbwZones(7);
            Assert.Throws<InvalidOperationException>(() =>
                InterfacePacker.Pack(zones, Lutron, new[] { Loop(zones, reserved: 5) }));
        }

        [Fact]
        public void LoopReservingWholeCeiling_Throws()
        {
            var zones = RgbwZones(1);
            Assert.Throws<InvalidOperationException>(() =>
                InterfacePacker.Pack(zones, Lutron, new[] { Loop(zones, reserved: 32) }));
        }

        [Fact]
        public void AutoPackedInterfaces_ReserveNothing()
        {
            // Undeclared zones auto-pack against the full ceiling — no reservation applies.
            var result = InterfacePacker.Pack(RgbwZones(6), Lutron);
            Assert.All(result.Interfaces, i => Assert.Equal(0, i.ReservedChannels));
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
            var result = InterfacePacker.Pack(zones, Lutron);

            var placedNames = result.Interfaces.SelectMany(i => i.Zones).Select(z => z.ZoneName).ToArray();
            Assert.Equal(zones.Length, placedNames.Length);          // none dropped
            Assert.Equal(zones.Length, placedNames.Distinct().Count()); // none duplicated/split
            Assert.All(result.Interfaces, i => Assert.True(i.ChannelsUsed <= result.ChannelCeiling));
        }
    }
}
