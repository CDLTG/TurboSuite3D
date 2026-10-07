#nullable disable
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Set-2 oracle suite for the location-affinity keypad pour (Phase C of the located-keypads plan;
    //  Core/Zones/Services/ControlLinkPacker.cs PourKeypadDevices). Located keypads pour into their own
    //  location's QS links first (isolation-free first), spilling only when full — so a location rides
    //  ONE link in the normal case (one clean list) and splits into honest per-link fragments only past
    //  the 99-device cap. The invariant these guard: affinity only REDISTRIBUTES within the fixed link
    //  budget — the processor/link COUNT never moves, and a job with no located records packs exactly
    //  as before (the frozen baselines, which carry no records, stay green on their own).
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    public class LocatedKeypadPourTests
    {
        private static ProcessorSlot Slot(int location) => new ProcessorSlot(location);

        private static KeypadRecord KR(int location, int devices = 1, string switchId = "") =>
            new KeypadRecord(switchId: switchId, room: "", model: "", location: location, devices: devices);

        /// <summary>A pure-keypad demand: FloatingDevices is the sum of record device weights, so the
        /// capacity math sees exactly the keypads the records describe.</summary>
        private static LinkDemand KeypadDemand(IEnumerable<KeypadRecord> records,
            IEnumerable<LinkUnit> pinned = null)
        {
            var recs = records.ToList();
            return new LinkDemand(
                pinnedUnits: pinned?.ToList(),
                floatingDevices: recs.Sum(r => r.Devices),
                keypadRecords: recs);
        }

        private static LinkUnit Module(int location) =>
            new LinkUnit("M", devices: 1, loads: 4, pdu: 0, category: LinkCategory.Modules, location: location);

        private static int Keypads(PackedLink link) =>
            link.Units.Where(u => u.Category == LinkCategory.Keypads).Sum(u => u.Devices);

        private static ProcessorGroup Proc(LinkPackResult r, int location) =>
            r.Processors.First(p => p.Location == location);

        // ── A location rides one link (one clean list) ────────────────────────────────────────────

        [Fact]
        public void EachLocationsKeypads_LandOnOneLinkInItsOwnProcessor()
        {
            // Two processors, locations 1 and 2. 10 keypads in each location.
            var procs = new List<ProcessorSlot> { Slot(1), Slot(2) };
            var records = Enumerable.Repeat(KR(1), 10).Concat(Enumerable.Repeat(KR(2), 10));
            var result = ControlLinkPacker.Pack(KeypadDemand(records), procs);

            var p1 = Proc(result, 1);
            var p2 = Proc(result, 2);

            // Each location's keypads ride its own processor, all on a single link ⇒ one list.
            Assert.Equal(10, Keypads(p1.Link1) + Keypads(p1.Link2));
            Assert.Equal(10, Keypads(p2.Link1) + Keypads(p2.Link2));
            Assert.Equal(1, new[] { p1.Link1, p1.Link2 }.Count(l => Keypads(l) > 0));
            Assert.Equal(1, new[] { p2.Link1, p2.Link2 }.Count(l => Keypads(l) > 0));
        }

        // ── Isolation: prefer the located-unit-free link in the location (rule #2) ─────────────────

        [Fact]
        public void LocatedKeypads_IsolateOntoTheFreeLinkInTheirLocation()
        {
            // One processor (location 1), a module on it, 20 keypads in location 1. The module lands on
            // link 1; the keypads isolate onto the empty link 2 rather than pile onto the module's link.
            var procs = new List<ProcessorSlot> { Slot(1) };
            var demand = KeypadDemand(Enumerable.Repeat(KR(1), 20), pinned: new[] { Module(1) });
            var result = ControlLinkPacker.Pack(demand, procs);

            var p1 = Proc(result, 1);
            Assert.Equal(0, Keypads(p1.Link1));   // the module's link — kept keypad-free
            Assert.Equal(20, Keypads(p1.Link2));  // the spare link absorbs the keypads
        }

        // ── Spill only when full → honest per-link fragments (the >99 case) ────────────────────────

        [Fact]
        public void OverflowingLocation_SplitsIntoPerLinkFragments_NotAcrossLocations()
        {
            // 120 keypads in location 1, one processor (two links, 99 each). The first link fills to 99,
            // the remainder (21) spills to the location's second link — a per-link fragment, not a jump
            // to another location.
            var procs = new List<ProcessorSlot> { Slot(1), Slot(2) };
            var result = ControlLinkPacker.Pack(KeypadDemand(Enumerable.Repeat(KR(1), 120)), procs);

            var p1 = Proc(result, 1);
            var p2 = Proc(result, 2);
            Assert.Equal(99, Keypads(p1.Link1));
            Assert.Equal(21, Keypads(p1.Link2));
            Assert.Equal(0, Keypads(p2.Link1) + Keypads(p2.Link2));   // location 2 untouched
        }

        // ── Two-gang weight (device count), not row count ──────────────────────────────────────────

        [Fact]
        public void TwoGangRecord_PoursAsTwoDevices()
        {
            var procs = new List<ProcessorSlot> { Slot(1) };
            // Three physical keypads, one two-gang → 1 + 1 + 2 = 4 devices on location 1's link.
            var records = new[] { KR(1), KR(1), KR(1, devices: 2) };
            var result = ControlLinkPacker.Pack(KeypadDemand(records), procs);

            Assert.Equal(4, Keypads(Proc(result, 1).Link1));
        }

        // ── Location-less keypads keep the job-wide pour ───────────────────────────────────────────

        [Fact]
        public void LocationLessKeypads_PourJobWide_AlongsideLocatedOnes()
        {
            // 10 located (loc 1) + 5 location-less. The located ride location 1; the 5 unmapped pour
            // job-wide (isolation-free first → the same processor-1 link 1 here, both free).
            var procs = new List<ProcessorSlot> { Slot(1), Slot(2) };
            var records = Enumerable.Repeat(KR(1), 10).Concat(Enumerable.Repeat(KR(0), 5));
            var result = ControlLinkPacker.Pack(KeypadDemand(records), procs);

            int total = result.Processors.Sum(p => Keypads(p.Link1) + Keypads(p.Link2));
            Assert.Equal(15, total);   // every device landed; none lost, none doubled
        }

        // ── C3: records are tagged to the link they land on ───────────────────────────────────────

        [Fact]
        public void EachLinkCarries_TheRecordsThatLandedOnIt()
        {
            var procs = new List<ProcessorSlot> { Slot(1), Slot(2) };
            var records = Enumerable.Repeat(KR(1), 10).Concat(Enumerable.Repeat(KR(2), 7));
            var result = ControlLinkPacker.Pack(KeypadDemand(records), procs);

            var p1 = Proc(result, 1);
            var p2 = Proc(result, 2);
            // One list per location, on one link; the record count == the located keypad count.
            Assert.Equal(10, p1.Link1.KeypadRecords.Count);
            Assert.Empty(p1.Link2.KeypadRecords);
            Assert.Equal(7, p2.Link1.KeypadRecords.Count);
        }

        [Fact]
        public void TwoGangThatDoesNotFit_RollsWhollyToTheNextLink()
        {
            // 98 singles then a two-gang (Switch IDs order the two-gang last). Link 1 fills to 98; the
            // two-gang needs 2 but has room for 1, so it rolls WHOLE to link 2 — never split 1+1.
            var procs = new List<ProcessorSlot> { Slot(1) };
            var singles = Enumerable.Range(1, 98).Select(i => KR(1, switchId: $"{i:000}"));
            var twoGang = KR(1, devices: 2, switchId: "099");
            var result = ControlLinkPacker.Pack(KeypadDemand(singles.Append(twoGang)), procs);

            var p1 = Proc(result, 1);
            Assert.Equal(98, Keypads(p1.Link1));
            Assert.Equal(98, p1.Link1.KeypadRecords.Count);
            Assert.Equal(2, Keypads(p1.Link2));                     // the two-gang, whole
            Assert.Single(p1.Link2.KeypadRecords);
            Assert.Equal(2, p1.Link2.KeypadRecords[0].Devices);
        }

        // ── RelabelLocations carries records through (orphan→host), not drops them ─────────────────

        [Fact]
        public void RelabelLocations_PreservesRecords_AndRelabelsOrphanLocations()
        {
            var demand = new LinkDemand(floatingDevices: 2, keypadRecords: new[] { KR(5), KR(2) });

            // No orphan map → demand returned unchanged, records intact (the bug was dropping them here).
            var same = ControlLinkPacker.RelabelLocations(demand, null);
            Assert.Equal(new[] { 2, 5 }, same.KeypadRecords.Select(r => r.Location).OrderBy(x => x).ToArray());

            // Orphan location 5 pools to host 1 → that record's location follows; location 2 is untouched.
            var relabel = ControlLinkPacker.RelabelLocations(demand,
                new Dictionary<int, int> { { 5, 1 } });
            Assert.Equal(new[] { 1, 2 }, relabel.KeypadRecords.Select(r => r.Location).OrderBy(x => x).ToArray());
        }

        // ── COUNT invariant: records never move the processor/link recommendation ──────────────────

        [Fact]
        public void KeypadRecords_DoNotChangeLinkCount_VsPlainPour()
        {
            // Same device totals, with vs without located records, pack to the same QS link count — the
            // affinity is distribution-only. (Mirrors KeypadsFillGapsRatherThanForcingLinks.)
            var panels = new List<PanelResult> { new PanelResult { PanelName = "1-A" } };
            panels[0].Modules.Add(new ModuleResult { ModuleCapacity = 4 });  // 1 device

            var located98 = Enumerable.Repeat(KR(1), 98).ToList();
            var located99 = Enumerable.Repeat(KR(1), 99).ToList();

            int WithRecords(int keypads, List<KeypadRecord> recs) =>
                ControlLinkPacker.Pack(
                    ControlLinkPacker.BuildDemand(panels,
                        new BomExtras { KeypadCount = keypads, KeypadRecords = recs })).QsLinkCount;

            Assert.Equal(1, WithRecords(98, located98));  // 1 panel device + 98 keypads = 99 ⇒ 1 link
            Assert.Equal(2, WithRecords(99, located99));  // 100 ⇒ 2 links — same as the record-free baseline
        }
    }
}
