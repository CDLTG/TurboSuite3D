using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for the Zones panel-breakdown allocator
    //  (Core/Zones/Services/PanelAllocationService.cs). Deterministic and Revit-free: given circuits
    //  + a BrandConfig it recommends panel counts and packs modules. Bugs here mis-size a client's
    //  lighting-control panel BOM.
    //
    //  For me (Claude): internal helpers are reached via InternalsVisibleTo (see Core.csproj). Each
    //  non-obvious expected value carries its derivation inline — re-derive from the comment before
    //  "fixing" a red assertion. BrandConfig.Lutron (cap 4, amp-limited) and .Crestron (cap 8, count-
    //  based, Relay cap 4) are the ready-made real configs used as fixtures.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Zone number is parsed from the panel name: "ZONE N" / "SHADE N" (case-insensitive) or the
    /// legacy "{n}-{letter}" form. Anything else → 0 (caller treats 0 as unassigned). Shades share the ZONE
    /// number space so SHADE 3 merges into Location 3.</summary>
    public class ParseLocationNumberTests
    {
        [Theory]
        [InlineData("ZONE 3", 3)]
        [InlineData("zone 12", 12)]        // case-insensitive
        [InlineData("ZONE 007", 7)]        // leading zeros parse
        [InlineData("SHADE 3", 3)]         // shade location shares the ZONE number space
        [InlineData("shade 1", 1)]         // case-insensitive
        [InlineData("SHADE 12", 12)]
        [InlineData("SHADE X", 0)]         // non-numeric → unassigned
        [InlineData("(unassigned)", 0)]    // the shade fallback bucket
        [InlineData("3-A", 3)]             // legacy number-letter
        [InlineData("12-B", 12)]
        [InlineData("0-A", 0)]             // legacy with zero → 0 (unassigned upstream)
        [InlineData("ZONE X", 0)]          // non-numeric zone
        [InlineData("ZONE ", 0)]           // empty number
        [InlineData("DUMMY", 0)]
        [InlineData("5", 0)]               // bare number, no recognized shape
        [InlineData("-5", 0)]              // dash at index 0 → not legacy form
        [InlineData("", 0)]
        [InlineData(null, 0)]
        public void ParseLocationNumber(string? panelName, int expected)
            => Assert.Equal(expected, PanelAllocationService.ParseLocationNumber(panelName));
    }

    /// <summary>Module count = circuits padded by 5% spare and divided by module capacity (ceil),
    /// but never leaving a wholly empty trailing module. The pull-back rule is the subtle part.</summary>
    public class CalculateModuleCountTests
    {
        // cap = 4 (Lutron). Derivations: req = ceil(count*1.05); m = ceil(req/4);
        // if (m-1)*4 >= count then m-- (don't add an empty module just to hold spare).
        [Theory]
        [InlineData(0, 4, 0)]
        [InlineData(1, 4, 1)]   // req=2, m=1
        [InlineData(4, 4, 1)]   // req=5, m=2, but (1)*4>=4 → 1
        [InlineData(5, 4, 2)]   // req=6, m=2, (1)*4>=5? no → 2
        [InlineData(8, 4, 2)]   // req=9, m=3, (2)*4>=8 → 2
        [InlineData(9, 4, 3)]   // req=10, m=3, (2)*4>=9? no → 3
        [InlineData(20, 4, 5)]  // req=21, m=6, (5)*4>=20 → 5 (spare absorbed, no 6th empty module)
        // cap = 8 (Crestron ELV)
        [InlineData(8, 8, 1)]   // req=9, m=2, (1)*8>=8 → 1
        [InlineData(9, 8, 2)]   // req=10, m=2, (1)*8>=9? no → 2
        [InlineData(16, 8, 2)]  // req=17, m=3, (2)*8>=16 → 2
        public void CalculateModuleCount(int circuits, int capacity, int expected)
            => Assert.Equal(expected, PanelAllocationService.CalculateModuleCount(circuits, capacity));
    }

    /// <summary>Types are emitted Relay → 0-10V → ELV first (the physical panel order), then any
    /// unknown types alphabetically. Casing follows the caller's actual strings.</summary>
    public class GetOrderedTypesTests
    {
        private static List<string> Order(params string[] types)
            => PanelAllocationService.GetOrderedTypes(types).ToList();

        [Fact]
        public void KnownTypes_InModuleOrder()
            => Assert.Equal(new[] { "Relay", "0-10V", "ELV" }, Order("ELV", "Relay", "0-10V"));

        [Fact]
        public void UnknownTypes_TrailAlphabetically_AfterKnown()
            => Assert.Equal(new[] { "0-10V", "ELV", "Foo" }, Order("ELV", "Foo", "0-10V"));

        [Fact]
        public void AllUnknown_Alphabetical()
            => Assert.Equal(new[] { "Apple", "Zebra" }, Order("Zebra", "Apple"));

        [Fact]
        public void PreservesCallerCasing()
            => Assert.Equal(new[] { "relay" }, Order("relay")); // matched case-insensitively, emitted as-given
    }

    /// <summary>Count-based module fill (brand with no amp limits, e.g. Crestron): circuits spread
    /// evenly across the reserved modules, capped at capacity.</summary>
    public class BuildModulesCountBasedTests
    {
        private static ModuleResult[] Build(int circuitCount, int moduleCount)
        {
            var circuits = Enumerable.Range(1, circuitCount)
                .Select(i => new ZonesCircuitData { CircuitNumber = i.ToString(), DimmingType = "ELV" })
                .ToList();
            // Crestron ELV: cap 8, no amp limits → count-based path.
            return PanelAllocationService
                .BuildModules("ELV", circuits, moduleCount, 8, BrandConfig.Crestron)
                .ToArray();
        }

        [Fact]
        public void SpreadsEvenly_AcrossModules()
        {
            // 10 circuits over 2 modules → ceil(10/2)=5 then ceil(5/1)=5 → [5,5].
            var mods = Build(10, 2);
            Assert.Equal(new[] { 5, 5 }, mods.Select(m => m.UsedSlots));
        }

        [Fact]
        public void UnevenSplit_FrontLoaded()
        {
            // 9 circuits over 2 modules → ceil(9/2)=5, then remaining 4 → [5,4].
            var mods = Build(9, 2);
            Assert.Equal(new[] { 5, 4 }, mods.Select(m => m.UsedSlots));
        }

        [Fact]
        public void SingleModule_HoldsAll()
            => Assert.Equal(new[] { 6 }, Build(6, 1).Select(m => m.UsedSlots));
    }

    /// <summary>Amp-aware module build (Lutron): over-default circuits are promoted to slot 1, and
    /// slot/total overloads are flagged. amps = ApparentLoadVA / 120.</summary>
    public class BuildModulesAmpAwareTests
    {
        private static ZonesCircuitData C(string number, double va)
            => new ZonesCircuitData { CircuitNumber = number, DimmingType = "ELV", ApparentLoadVA = va };

        // Lutron ELV → LQSE-4A5: slot1 6.6A, default 4.2A, total 16A, 120V, module cap 4.
        private static List<ModuleResult> Build(params ZonesCircuitData[] circuits)
            => PanelAllocationService.BuildModules("ELV", circuits.ToList(), 1, 4, BrandConfig.Lutron);

        [Fact]
        public void OverDefaultCircuit_PromotedToSlot1_NoOverload()
        {
            // c1 = 120VA → 1.0A; c2 = 600VA → 5.0A (> 4.2 default, < 6.6 slot1).
            // Natural order is [c1,c2]; the 5.0A load promotes ahead of the 1.0A into slot 0.
            var m = Build(C("1", 120), C("2", 600)).Single();
            Assert.Equal(new[] { "2", "1" }, m.CircuitNumbers);
            Assert.Equal(5.0, m.SlotAmps[0], precision: 6);
            Assert.False(m.IsOverloaded); // 5.0<6.6 slot1, total 6.0<16
        }

        [Fact]
        public void ExceedingSlot1Limit_FlagsOverload()
        {
            // 840VA → 7.0A > 6.6A slot-1 limit → overloaded.
            var m = Build(C("1", 840)).Single();
            Assert.Equal(7.0, m.SlotAmps[0], precision: 6);
            Assert.True(m.IsOverloaded);
        }

        /// <summary>SlotProtocols tracks the reordering: slot-1 promotion moves the circuit AND
        /// its protocol together, so the panel schedule can't print one slot's protocol against
        /// another slot's load.</summary>
        [Fact]
        public void SlotProtocols_FollowSlot1Promotion()
        {
            var quiet = C("1", 120);
            quiet.DimmingProtocolDisplay = "ELV";
            var promoted = C("2", 600);
            promoted.DimmingProtocolDisplay = "MLV";

            var m = Build(quiet, promoted).Single();

            Assert.Equal(new[] { "2", "1" }, m.CircuitNumbers);   // promoted first
            Assert.Equal(new[] { "MLV", "ELV" }, m.SlotProtocols); // protocols came along
            Assert.Equal("MLV", m.SlotProtocol(0));
            Assert.Equal("ELV", m.SlotProtocol(1));
        }

        /// <summary>The module stays ELV (that is what gets ordered) while its slot reports MLV
        /// (that is what the output is configured for). Conflating them is the bug this exists
        /// to prevent — don't "simplify" SlotProtocol away to DimmingType.</summary>
        [Fact]
        public void ModuleTypeAndSlotProtocol_DivergeForMlv()
        {
            var mlv = C("1", 120);
            mlv.DimmingProtocolDisplay = "MLV";

            var m = Build(mlv).Single();

            Assert.Equal("ELV", m.DimmingType);
            Assert.Equal("LQSE-4A5-120-D", m.PartNumber);
            Assert.Equal("MLV", m.SlotProtocol(0));
        }

        /// <summary>Falls back to the module type when no protocol was recorded, so an
        /// unpopulated path degrades to the old behavior rather than printing blank.</summary>
        [Fact]
        public void SlotProtocol_FallsBackToModuleType()
        {
            var m = Build(C("1", 120)).Single(); // C() leaves DimmingProtocolDisplay null

            Assert.Equal("ELV", m.SlotProtocol(0));
            Assert.Equal("ELV", m.SlotProtocol(99)); // out of range → still safe
        }
    }

    /// <summary>BOM grouping: modules collapse by part number, ordered by module type rank
    /// (Relay → 0-10V → ELV) then part number.</summary>
    public class GroupModulesByPartNumberTests
    {
        private static ModuleResult M(string part, string type)
            => new ModuleResult { PartNumber = part, DimmingType = type };

        [Fact]
        public void GroupsByPart_OrderedByTypeRank()
        {
            var grouped = PanelAllocationService.GroupModulesByPartNumber(new[]
            {
                M("LQSE-4A5-120-D", "ELV"),
                M("LQSE-4A5-120-D", "ELV"),
                M("LQSE-4T5-120-D", "0-10V"),
                M("LQSE-4S8-120-D", "Relay"),
            }).ToArray();

            // Relay(0) → 0-10V(1) → ELV(2); counts collapsed per part.
            Assert.Equal(new[]
            {
                ("LQSE-4S8-120-D", 1),
                ("LQSE-4T5-120-D", 1),
                ("LQSE-4A5-120-D", 2),
            }, grouped);
        }
    }

    /// <summary>End-to-end BuildPanelBreakdown orchestration: zone grouping, DUMMY exclusion,
    /// switch-wired vs. genuinely-unassigned handling, and zone ordering.</summary>
    public class BuildPanelBreakdownTests
    {
        private static ZonesCircuitData C(string number, string? panel, string type = "ELV",
            bool wiredToSwitch = false)
            => new ZonesCircuitData
            {
                CircuitNumber = number,
                PanelName = panel,
                DimmingType = type,
                IsWiredToSwitch = wiredToSwitch,
            };

        [Fact]
        public void GroupsCircuitsByZone_IntoRecommendedPanels()
        {
            // 4 ELV circuits in ZONE 1. Crestron ELV cap 8 → 1 module; default panel size 7 → 1 panel "1-A".
            var circuits = Enumerable.Range(1, 4).Select(i => C(i.ToString(), "ZONE 1")).ToList();
            var (result, unassigned) = PanelAllocationService.BuildPanelBreakdown(circuits, BrandConfig.Crestron);

            Assert.Empty(unassigned);
            var loc = Assert.Single(result.Locations);
            Assert.Equal(1, loc.LocationNumber);
            var panel = Assert.Single(loc.Panels);
            Assert.Equal("1-A", panel.PanelName);
            Assert.Equal(1, loc.TotalModules);
            Assert.Equal(4, panel.Modules.Single().UsedSlots);
        }

        [Fact]
        public void DummyPanel_ExcludedEntirely()
        {
            var (result, unassigned) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData> { C("1", "DUMMY") }, BrandConfig.Crestron);

            Assert.Empty(result.Locations);
            Assert.Empty(unassigned); // DUMMY is intentional, not a warning
        }

        [Fact]
        public void BlankPanel_UnassignedUnlessSwitchWired()
        {
            var (_, unassigned) = PanelAllocationService.BuildPanelBreakdown(new List<ZonesCircuitData>
            {
                C("1", ""),                                   // genuinely unassigned → warned
                C("2", null, wiredToSwitch: true),            // switch-wired → legitimately unpaneled
            }, BrandConfig.Crestron);

            var lone = Assert.Single(unassigned);
            Assert.Equal("1", lone.CircuitNumber);
        }

        /// <summary>A benched (NotYetSupported) or off-vocabulary (NoProtocol) circuit is surfaced loudly —
        /// it wants a panel but has no module to sit on, so it must appear rather than become a phantom BOM
        /// part. (Uses a placeholder protocol: DALI itself is no longer NotYetSupported — it became a
        /// subsystem in Phase 3 — but the allocator's handling of the outcome is what's under test here.)</summary>
        [Theory]
        [InlineData(DimmingResolveOutcome.NotYetSupported)]
        [InlineData(DimmingResolveOutcome.NoProtocol)]
        public void NonAllocatableProtocol_ExcludedAndWarned(DimmingResolveOutcome outcome)
        {
            var circuit = C("1", "ZONE 1");
            circuit.DimmingType = string.Empty;   // resolver emits no module key for these
            circuit.DimmingOutcome = outcome;
            circuit.DimmingProtocolDisplay = "SOME-UNMODELED";

            var (result, unassigned) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData> { circuit }, BrandConfig.Crestron);

            Assert.Empty(result.Locations);       // no zone, so no panel and no module
            var lone = Assert.Single(unassigned);
            Assert.Equal("SOME-UNMODELED", lone.DimmingProtocolDisplay);
        }

        /// <summary>WIFI is excluded the same way but stays SILENT — it is network-controlled and
        /// legitimately rides no module, mirroring the switch-wired exclusion. Landing in the
        /// Unassigned list would train users to ignore that list.</summary>
        [Fact]
        public void NoModuleByDesign_ExcludedSilently()
        {
            var wifi = C("1", "ZONE 1");
            wifi.DimmingType = string.Empty;
            wifi.DimmingOutcome = DimmingResolveOutcome.NoModuleByDesign;

            // Unpaneled too — a WIFI circuit typically has no zone panel, and that must not
            // trip the blank-panel warning either.
            var wifiNoPanel = C("2", "");
            wifiNoPanel.DimmingType = string.Empty;
            wifiNoPanel.DimmingOutcome = DimmingResolveOutcome.NoModuleByDesign;

            var (result, unassigned) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData> { wifi, wifiNoPanel }, BrandConfig.Crestron);

            Assert.Empty(result.Locations);
            Assert.Empty(unassigned);
        }

        /// <summary>A DMX circuit whose subsystem accounted for it is excluded silently — for the
        /// opposite reason to WIFI: its control hardware very much exists, and TurboDMX counted the
        /// QSE-CI-DMX interfaces. Flagging it would ask the user to fix something already handled.</summary>
        [Fact]
        public void HandledBySubsystem_ExcludedSilently_WhenTheSubsystemAccountedForIt()
        {
            var dmx = Dmx("1", "ZONE 1");

            // The DMX tape is often on its own unpaneled feed, which must not trip the blank-panel
            // warning either.
            var dmxNoPanel = Dmx("2", "");

            var (result, unassigned) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData> { dmx, dmxNoPanel }, BrandConfig.Crestron,
                subsystemDemands: new[] { DmxDemand(interfaces: 2) });

            Assert.Empty(result.Locations);   // rides no DIN module, so it builds no panel
            Assert.Empty(unassigned);
        }

        /// <summary>A subsystem that reported a REASON has also accounted for itself — the user is
        /// already being told what to fix, and listing the circuits too would be the same complaint
        /// twice.</summary>
        [Fact]
        public void HandledBySubsystem_StaysSilent_WhenTheSubsystemReportedADiagnostic()
        {
            var (_, unassigned) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData> { Dmx("1", "ZONE 1") }, BrandConfig.Crestron,
                subsystemDemands: new[]
                {
                    ControlSubsystemDemand.Unsolvable("DMX", "no decoder type is selected")
                });

            Assert.Empty(unassigned);
        }

        /// <summary>The case this rule exists for. The fixtures declare DMX but carry no DMX Channels,
        /// so TurboDMX never saw them: no parts, no reason, nothing. Silence here would mean the
        /// circuits vanish from the breakdown, order nothing, and are mentioned nowhere at all — so
        /// they go back in the Unassigned list, where they lived before subsystems existed.</summary>
        [Fact]
        public void HandledBySubsystem_Surfaces_WhenNoSubsystemAccountedForIt()
        {
            var (_, unassigned) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData> { Dmx("1", "ZONE 1") }, BrandConfig.Crestron,
                subsystemDemands: new[] { ControlSubsystemDemand.None("DMX") });

            Assert.Equal("DMX", Assert.Single(unassigned).DimmingProtocolDisplay);
        }

        /// <summary>No demands supplied at all (the Panel Schedule path, and any caller predating the
        /// seam) surfaces them too. Safe direction: a circuit nobody vouched for is visible.</summary>
        [Fact]
        public void HandledBySubsystem_Surfaces_WhenNoDemandsWereSuppliedAtAll()
        {
            var (_, unassigned) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData> { Dmx("1", "ZONE 1") }, BrandConfig.Crestron);

            Assert.Single(unassigned);
        }

        /// <summary>Accounting is per subsystem: DALI speaking up must not silence DMX's circuits.</summary>
        [Fact]
        public void OneSubsystemAccounting_DoesNotSilenceAnother()
        {
            var (_, unassigned) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData> { Dmx("1", "ZONE 1") }, BrandConfig.Crestron,
                subsystemDemands: new[] { ControlSubsystemDemand.Unsolvable("DALI", "no module data") });

            Assert.Single(unassigned);
        }

        /// <summary>A subsystem-owned circuit is still never allocated, accounted for or not — so the
        /// panels built are identical either way and only the Unassigned list moves.</summary>
        [Fact]
        public void SubsystemAccounting_DoesNotChangeTheAllocation()
        {
            var circuits = new List<ZonesCircuitData> { C("1", "ZONE 1"), Dmx("2", "ZONE 1") };

            var (accounted, _) = PanelAllocationService.BuildPanelBreakdown(
                circuits, BrandConfig.Crestron, subsystemDemands: new[] { DmxDemand(1) });
            var (unaccounted, _) = PanelAllocationService.BuildPanelBreakdown(
                circuits, BrandConfig.Crestron, subsystemDemands: new[] { ControlSubsystemDemand.None("DMX") });

            Assert.Equal(accounted.AllPanels.Count, unaccounted.AllPanels.Count);
            Assert.Equal(accounted.AllPanels.Sum(p => p.Modules.Count),
                         unaccounted.AllPanels.Sum(p => p.Modules.Count));
        }

        private static ZonesCircuitData Dmx(string number, string panel)
        {
            var circuit = C(number, panel);
            circuit.DimmingType = string.Empty;
            circuit.DimmingOutcome = DimmingResolveOutcome.HandledBySubsystem;
            circuit.DimmingSubsystem = "DMX";
            circuit.DimmingProtocolDisplay = "DMX";
            return circuit;
        }

        private static ControlSubsystemDemand DmxDemand(int interfaces) =>
            new ControlSubsystemDemand("DMX",
                parts: new List<DemandPart>
                {
                    new DemandPart("QSE-CI-DMX", interfaces, DemandMount.LvCompartment)
                },
                linkDevices: interfaces);

        /// <summary>A DMX circuit alongside an allocatable one leaves the zone's panel untouched —
        /// the subsystem takes its circuit out of the allocation entirely, it does not shrink it.</summary>
        [Fact]
        public void HandledBySubsystem_DoesNotDisturbItsZone()
        {
            var dmx = C("2", "ZONE 1");
            dmx.DimmingType = string.Empty;
            dmx.DimmingOutcome = DimmingResolveOutcome.HandledBySubsystem;

            var (withDmx, _) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData> { C("1", "ZONE 1"), dmx }, BrandConfig.Crestron);
            var (without, _) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData> { C("1", "ZONE 1") }, BrandConfig.Crestron);

            Assert.Equal(without.AllPanels.Count, withDmx.AllPanels.Count);
            Assert.Equal(without.AllPanels.Sum(p => p.Modules.Count),
                         withDmx.AllPanels.Sum(p => p.Modules.Count));
        }

        /// <summary>SlotProtocols is populated through the count-based path too (Crestron has no
        /// amp limits, so it takes the other BuildModules branch).</summary>
        [Fact]
        public void SlotProtocols_PopulatedOnCountBasedPath()
        {
            var elv = C("1", "ZONE 1");
            elv.DimmingProtocolDisplay = "ELV";
            var mlv = C("2", "ZONE 1");
            mlv.DimmingProtocolDisplay = "MLV";

            var (result, _) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData> { elv, mlv }, BrandConfig.Crestron);

            var module = result.Locations.Single().Panels.Single().Modules.Single();
            Assert.Equal(new[] { "ELV", "MLV" }, module.SlotProtocols);
            Assert.Equal("ELV", module.DimmingType); // both ride one ELV module
        }

        /// <summary>An allocatable circuit alongside a benched one still builds its panel —
        /// one bad protocol doesn't take the zone down with it.</summary>
        [Fact]
        public void BenchedCircuit_DoesNotBlockItsZone()
        {
            var dali = C("2", "ZONE 1");
            dali.DimmingType = string.Empty;
            dali.DimmingOutcome = DimmingResolveOutcome.NotYetSupported;

            var (result, unassigned) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData> { C("1", "ZONE 1"), dali }, BrandConfig.Crestron);

            var loc = Assert.Single(result.Locations);
            Assert.Equal(1, Assert.Single(loc.Panels).Modules.Single().UsedSlots); // only circuit "1"
            Assert.Equal("2", Assert.Single(unassigned).CircuitNumber);
        }

        [Fact]
        public void UnparseablePanelName_Unassigned()
        {
            var (_, unassigned) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData> { C("1", "GARBAGE") }, BrandConfig.Crestron);
            Assert.Single(unassigned);
        }

        [Fact]
        public void MultipleZones_OrderedAscending()
        {
            var (result, _) = PanelAllocationService.BuildPanelBreakdown(new List<ZonesCircuitData>
            {
                C("1", "ZONE 3"),
                C("2", "ZONE 1"),
                C("3", "ZONE 2"),
            }, BrandConfig.Crestron);

            Assert.Equal(new[] { 1, 2, 3 }, result.Locations.Select(l => l.LocationNumber));
        }
    }

    /// <summary>
    /// Relay+0-10V packing (the max-packing toggle). Enabled AND both dimming types sharing one physical
    /// module (Lutron non-dedicated → both LQSE-4T5) folds them into a single protocol-clustered pool:
    /// pure-relay modules, one seam module at the boundary, then pure-0-10V — reclaiming the spare slots
    /// the split wasted. The flag is silently ignored wherever the modules differ (dedicated LQSE-4S8,
    /// Crestron), and the mixed module labels itself "RELAY / 0-10V" off its slots rather than its
    /// synthetic "0-10V" sort key.
    /// </summary>
    public class MergeRelayZeroTenTests
    {
        // Loads kept at 1.0 A each — well under the LQSE-4T5's 5 A slot / 20 A module limits — so the
        // sequential seam layout stands and the amp-aware FFD fallback never fires.
        private static ZonesCircuitData C(string number, string type)
            => new ZonesCircuitData
            {
                CircuitNumber = number,
                PanelName = "ZONE 1",
                DimmingType = type,
                DimmingProtocolDisplay = string.Equals(type, "Relay", System.StringComparison.OrdinalIgnoreCase) ? "RELAY" : type,
                ApparentLoadVA = 120,
            };

        private static List<ZonesCircuitData> Circuits(int relay, int zeroTen)
        {
            var list = new List<ZonesCircuitData>();
            for (int i = 0; i < relay; i++) list.Add(C($"R{i:00}", "Relay"));
            for (int i = 0; i < zeroTen; i++) list.Add(C($"V{i:00}", "0-10V"));
            return list;
        }

        private static List<ModuleResult> Modules(PanelAllocationResult result)
            => result.Locations.SelectMany(l => l.Panels).SelectMany(p => p.Modules).ToList();

        /// <summary>6 relay + 1 0-10V. Split → 3 modules (2 relay + 1 0-10V, two half-empty spares).
        /// Merged → 2: one pure-relay, one seam. The seam is where the two former spares combine.</summary>
        [Fact]
        public void MergesIntoOnePool_WithOnePureModuleAndOneSeam()
        {
            var circuits = Circuits(relay: 6, zeroTen: 1);

            var (merged, _) = PanelAllocationService.BuildPanelBreakdown(
                Circuits(relay: 6, zeroTen: 1), BrandConfig.Lutron, allowRelayZeroTenPacking: true);
            var (split, _) = PanelAllocationService.BuildPanelBreakdown(
                circuits, BrandConfig.Lutron, allowRelayZeroTenPacking: false);

            // Split: ceil(6*1.05/4)=2 relay + ceil(1*1.05/4)=1 0-10V = 3.
            Assert.Equal(3, Modules(split).Count);

            // Merged: 7 loads → ceil(7*1.05/4)=2. Gradient order: pure relay first, then the seam.
            var mods = Modules(merged);
            Assert.Equal(2, mods.Count);
            Assert.Equal("RELAY", mods[0].TypeLabel);
            Assert.Equal(4, mods[0].UsedSlots);
            Assert.Equal("RELAY / 0-10V", mods[1].TypeLabel);
            Assert.Equal(3, mods[1].UsedSlots);
        }

        /// <summary>When the relay count is a multiple of the module capacity the boundary aligns to a
        /// module edge, so there is NO seam — both sides stay pure.</summary>
        [Fact]
        public void AlignedBoundary_ProducesNoMixedModule()
        {
            var (result, _) = PanelAllocationService.BuildPanelBreakdown(
                Circuits(relay: 4, zeroTen: 4), BrandConfig.Lutron, allowRelayZeroTenPacking: true);

            var mods = Modules(result);
            Assert.Equal(2, mods.Count);                                   // 8 loads → 2 modules
            Assert.Equal("RELAY", mods[0].TypeLabel);
            Assert.Equal("0-10V", mods[1].TypeLabel);
            Assert.DoesNotContain(mods, m => m.TypeLabel.Contains("/"));   // nothing mixed
        }

        /// <summary>The dedicated relay module (LQSE-4S8 ≠ LQSE-4T5) makes the merge physically invalid,
        /// so the flag is ignored — relay and 0-10V stay on separate modules.</summary>
        [Fact]
        public void DedicatedRelayModule_SuppressesMerge_EvenWhenFlagSet()
        {
            var (result, _) = PanelAllocationService.BuildPanelBreakdown(
                Circuits(relay: 6, zeroTen: 1),
                BrandConfig.CreateLutron(useDedicatedRelayModule: true),
                allowRelayZeroTenPacking: true);

            var mods = Modules(result);
            Assert.Equal(3, mods.Count);                                   // 2 relay (LQSE-4S8) + 1 0-10V
            Assert.DoesNotContain(mods, m => m.TypeLabel.Contains("/"));
        }

        /// <summary>Crestron's relay module (CLX-4HSW4) differs from its 0-10V module too, so the guard
        /// suppresses the merge there as well — the engine stays correct even though the UI never offers
        /// the toggle for Crestron.</summary>
        [Fact]
        public void Crestron_SuppressesMerge_EvenWhenFlagSet()
        {
            var (result, _) = PanelAllocationService.BuildPanelBreakdown(
                Circuits(relay: 6, zeroTen: 1), BrandConfig.Crestron, allowRelayZeroTenPacking: true);

            Assert.DoesNotContain(Modules(result), m => m.TypeLabel.Contains("/"));
        }

        /// <summary>Even with the flag on, a non-merged bucket (ELV) is untouched: an MLV load riding an
        /// ELV module still reads "ELV" on the tile, not "MLV". Only the merged Relay+0-10V pool labels
        /// itself from its slots.</summary>
        [Fact]
        public void NonMergedBucket_LabelsByModuleType_NotSlotProtocol()
        {
            var mlv = new ZonesCircuitData
            {
                CircuitNumber = "1", PanelName = "ZONE 1",
                DimmingType = "ELV", DimmingProtocolDisplay = "MLV", ApparentLoadVA = 120
            };

            var (result, _) = PanelAllocationService.BuildPanelBreakdown(
                new List<ZonesCircuitData> { mlv }, BrandConfig.Lutron, allowRelayZeroTenPacking: true);

            var module = Modules(result).Single();
            Assert.False(module.LabelFromSlotProtocols);
            Assert.Equal("ELV", module.TypeLabel);
            Assert.Equal("MLV", module.SlotProtocol(0));   // per-slot truth still intact
        }
    }
}
