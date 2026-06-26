using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx;
using TurboSuite.Dmx.Lock;
using TurboSuite.Dmx.Placement;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Oracles for <see cref="DmxPlacementPlanner"/> — the bill→placement-plan walk (BuildPlan Phase 2). Locks
    /// the global "DEC n" Switch-ID numbering (same order BillReport numbers decoders), the loop grouping, and
    /// the decoder/driver name→loaded-family-id mapping (including the unmapped-name = null path).
    /// </summary>
    public class DmxPlacementPlannerTests
    {
        private const double V = 24.0;

        private static DmxContract Contract(int ceiling = 32) => new DmxContract(
            decoderPool: new[] { DecoderSpec.Dmx4_5000_10A, DecoderSpec.Dmx6_22K },
            driverPool: new[] { new DriverType("320", 320.0, V, 0.0),
                                new DriverType("480", 480.0, V, 0.0),
                                new DriverType("600", 600.0, V, 0.0) },
            systemVolts: V, channelCeiling: ceiling, reservedChannels: 0, maxDevicesPerSegment: 32);

        private static readonly Dictionary<string, string> DecoderIds = new Dictionary<string, string>
        {
            ["4ch (DMX-4-5000-10A)"] = "dec4-uid",
            ["6ch (DMX-6-22K)"] = "dec6-uid",
        };
        private static readonly Dictionary<string, string> DriverIds = new Dictionary<string, string>
        {
            ["320"] = "d320-uid", ["480"] = "d480-uid", ["600"] = "d600-uid",
        };

        private static DmxNumbering Fresh(DmxBill bill) =>
            DmxLockReconciler.Reconcile(DmxBillFlattener.Flatten(bill), baseline: null, locked: false);

        private static ZoneDesign Zone(string name, int decoders) =>
            // Each TapeRun(94.3 W-ish, 4ch) ~490 W; sized so `decoders` decoders fall out of the pack.
            new ZoneDesign(name, Enumerable.Range(0, decoders)
                .Select(_ => new TapeRun(94.3, 5.2, channels: 4)).ToArray());

        [Fact]
        public void NumbersDecodersSequentiallyAndMapsTypes()
        {
            var bill = DmxSolver.Solve(Contract(), new[] { Zone("Z1", 2), Zone("Z2", 1) });
            var plan = DmxPlacementPlanner.Build(bill, Fresh(bill), DecoderIds, DriverIds);

            Assert.Equal(bill.TotalDecoders, plan.DeviceCount);

            var all = plan.Loops.SelectMany(l => l.Devices).ToList();
            Assert.Equal(Enumerable.Range(1, all.Count).Select(n => $"DEC {n}").ToList(),
                         all.Select(d => d.SwitchId).ToList());

            // Every device resolved its 4-ch decoder + a driver id from the curated maps.
            Assert.All(all, d => Assert.Equal("dec4-uid", d.DecoderTypeId));
            Assert.All(all, d => Assert.Contains(d.DriverTypeId, new[] { "d320-uid", "d480-uid", "d600-uid" }));
        }

        [Fact]
        public void GlobalNumberingContinuesAcrossDeclaredLoops()
        {
            // Two declared loops ⇒ two interfaces; DEC numbering must run continuously 1..N across them.
            var zones = new[] { Zone("Z1", 1), Zone("Z2", 1) };
            var loops = new[]
            {
                new LoopDeclaration("A", new[] { "Z1" }),
                new LoopDeclaration("B", new[] { "Z2" }),
            };
            var bill = DmxSolver.Solve(Contract(), zones, loops);
            var plan = DmxPlacementPlanner.Build(bill, Fresh(bill), DecoderIds, DriverIds);

            Assert.Equal(2, plan.LoopCount);
            Assert.Equal("A", plan.Loops[0].LoopName);
            Assert.Equal("B", plan.Loops[1].LoopName);

            var ids = plan.Loops.SelectMany(l => l.Devices).Select(d => d.SwitchId).ToList();
            Assert.Equal(new[] { "DEC 1", "DEC 2" }, ids);
        }

        [Fact]
        public void UnmappedTypeNameYieldsNullIdNotException()
        {
            var bill = DmxSolver.Solve(Contract(), new[] { Zone("Z1", 1) });
            // Empty maps ⇒ nothing resolves; the plan still builds, ids null (shim skips + warns).
            var plan = DmxPlacementPlanner.Build(bill, Fresh(bill),
                new Dictionary<string, string>(), new Dictionary<string, string>());

            var dev = Assert.Single(plan.Loops.SelectMany(l => l.Devices));
            Assert.Null(dev.DecoderTypeId);
            Assert.Null(dev.DriverTypeId);
            Assert.Equal("DEC 1", dev.SwitchId);
            Assert.Equal("4ch (DMX-4-5000-10A)", dev.DecoderName);
        }
    }
}
