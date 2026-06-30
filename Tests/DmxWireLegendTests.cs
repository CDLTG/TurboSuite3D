using System.Linq;
using TurboSuite.Dmx;
using TurboSuite.Dmx.OneLine;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    /// <summary>
    /// Oracles for the BuildPlan Phase 6 conductor engine + per-job wire legend: the uncapped
    /// channels→#16-N math (with the job-wide pull-up), and the dense, per-job sequential numbering that
    /// skips unused sizes (matches the firm's sample, <c>Specs/_DMX/Legend.txt</c>).
    /// </summary>
    public class DmxWireLegendTests
    {
        // ── Conductor math: channels + 1 common, rounded to the next even stock size, UNCAPPED ──────────
        [Theory]
        [InlineData(1, 2)]   // 1 ch + common = 2
        [InlineData(2, 4)]   // 3 → 4
        [InlineData(3, 4)]   // 4 → 4
        [InlineData(4, 6)]   // 5 → 6 (RGBW)
        [InlineData(5, 6)]   // 6 → 6
        [InlineData(6, 8)]   // 7 → 8  ← the headline uncap: a 6-channel RGBATW tape is #16-8, not #16-6
        [InlineData(8, 10)]  // 9 → 10
        public void HomerunConductors_AreExactAndUncapped(int channels, int conductors)
        {
            Assert.Equal(conductors, DmxWireLegend.HomerunConductors(channels, pullUpSizes: 0));
            Assert.Equal(conductors, DmxWireLegend.HomerunFor(channels).Conductors);
        }

        [Theory]
        [InlineData(4, 0, 6)]    // exact
        [InlineData(4, 1, 8)]    // bump one stock size
        [InlineData(4, 2, 10)]   // bump two
        [InlineData(1, 1, 4)]    // #16-2 → #16-4
        public void PullUp_BumpsEveryHomerunByStockSizes(int channels, int pullUp, int conductors)
            => Assert.Equal(conductors, DmxWireLegend.HomerunConductors(channels, pullUp));

        // ── Legend: the first three are always present and always 1–2–3 ────────────────────────────────
        [Fact]
        public void FixedCategories_AreAlwaysOneTwoThree_EvenWithNoLowVoltage()
        {
            var legend = DmxWireLegend.Build(System.Array.Empty<DmxWireType>());

            Assert.Equal(1, legend.NumberFor(DmxWireType.Hv));
            Assert.Equal(2, legend.NumberFor(DmxWireType.Cat6));
            Assert.Equal(3, legend.NumberFor(DmxWireType.Comm));
            Assert.Equal(3, legend.Entries.Count);
        }

        [Fact]
        public void Labels_MatchTheFirmsLegendSample()
        {
            Assert.Equal("#12-2 Line Voltage", DmxWireType.Hv.Label);
            Assert.Equal("CAT6 Network Cable", DmxWireType.Cat6.Label);
            Assert.Equal("Control Communication Wire", DmxWireType.Comm.Label);
            Assert.Equal("#16-2 Stranded Low Voltage", DmxWireType.Lv(2).Label);
            Assert.Equal("#16-6 Stranded Low Voltage", DmxWireType.Lv(6).Label);
        }

        // ── Dense per-job numbering: skip the unused size (the Legend.txt worked example) ───────────────
        [Fact]
        public void LowVoltage_NumbersDenselyFromFour_SkippingUnusedSizes()
        {
            // Job uses #16-2 + #16-6 (no #16-4): #16-2 ⇒ 4, #16-6 ⇒ 5 (NOT 6).
            var legend = DmxWireLegend.Build(new[] { DmxWireType.Lv(6), DmxWireType.Lv(2) });

            Assert.Equal(4, legend.NumberFor(DmxWireType.Lv(2)));
            Assert.Equal(5, legend.NumberFor(DmxWireType.Lv(6)));
            Assert.Equal(0, legend.NumberFor(DmxWireType.Lv(4)));   // unused ⇒ not in the legend
            Assert.Equal(5, legend.Entries.Count);                  // 3 fixed + 2 LV
        }

        [Fact]
        public void Numbering_IsPerJob_SoTheSameSizeShiftsBetweenJobs()
        {
            // Second job uses #16-2/4/6/8 ⇒ #16-6 becomes 6 here (it was 5 above) — per-job by design.
            var legend = DmxWireLegend.Build(new[]
            {
                DmxWireType.Lv(2), DmxWireType.Lv(4), DmxWireType.Lv(6), DmxWireType.Lv(8),
            });

            Assert.Equal(4, legend.NumberFor(DmxWireType.Lv(2)));
            Assert.Equal(5, legend.NumberFor(DmxWireType.Lv(4)));
            Assert.Equal(6, legend.NumberFor(DmxWireType.Lv(6)));
            Assert.Equal(7, legend.NumberFor(DmxWireType.Lv(8)));
        }

        [Fact]
        public void DuplicateSizes_CollapseToOneRow()
        {
            var legend = DmxWireLegend.Build(new[] { DmxWireType.Lv(6), DmxWireType.Lv(6), DmxWireType.Lv(2) });
            Assert.Equal(5, legend.Entries.Count);   // 3 fixed + #16-2 + #16-6 (the duplicate #16-6 collapses)
        }

        // ── ForBill: jumper #16-2 is always present, shared with a 1-channel homerun ────────────────────
        [Fact]
        public void ForBill_AddsTheSharedSixteenTwoJumper_EvenWhenAllZonesAreMultiChannel()
        {
            // One 4-channel zone (homerun #16-6). The 24 V driver→decoder jumper is #16-2 and must still
            // appear in the legend (shared number with a 1-ch homerun), so #16-2 ⇒ 4, #16-6 ⇒ 5.
            var bill = DmxSolver.Solve(Contract(), new[] { Zone("Z1", channels: 4) });
            var legend = DmxWireLegend.ForBill(bill, pullUpSizes: 0);

            Assert.Equal(4, legend.NumberFor(DmxWireType.Lv(2)));   // the jumper
            Assert.Equal(5, legend.NumberFor(DmxWireType.Lv(6)));   // the 4-ch homerun
        }

        [Fact]
        public void ForBill_PullUp_ShiftsTheHomerunSize()
        {
            var bill = DmxSolver.Solve(Contract(), new[] { Zone("Z1", channels: 4) });
            var legend = DmxWireLegend.ForBill(bill, pullUpSizes: 1);

            // Homerun pulls up #16-6 → #16-8; the jumper #16-2 is unaffected (it's not a homerun).
            Assert.Equal(4, legend.NumberFor(DmxWireType.Lv(2)));
            Assert.Equal(0, legend.NumberFor(DmxWireType.Lv(6)));   // no exact-6 homerun any more
            Assert.Equal(5, legend.NumberFor(DmxWireType.Lv(8)));
        }

        // ── Legend view layout (the sheet-placeable drafting view) ──────────────────────────────────────
        [Fact]
        public void LegendView_HasTitle_PlusOneMarkerAndLabelPerEntry()
        {
            var legend = DmxWireLegend.Build(new[] { DmxWireType.Lv(2), DmxWireType.Lv(6) });
            var drawing = DmxWireLegendPlanner.Build(legend);

            Assert.Equal(legend.Entries.Count, drawing.Markers.Count);          // one circled # per row
            Assert.Equal(legend.Entries.Count + 1, drawing.Notes.Count);        // one label per row + the title
            Assert.Equal(DmxOneLineGeometry.Legend.Title, drawing.Notes[0].Text);
        }

        [Fact]
        public void LegendView_MarkerNumbersAndLabels_MatchTheLegendEntries()
        {
            var legend = DmxWireLegend.Build(new[] { DmxWireType.Lv(2), DmxWireType.Lv(6) });
            var drawing = DmxWireLegendPlanner.Build(legend);

            for (int i = 0; i < legend.Entries.Count; i++)
            {
                Assert.Equal(legend.Entries[i].Number, drawing.Markers[i].Number);   // # on the view = legend #
                Assert.Equal(legend.Entries[i].Type, drawing.Markers[i].Type);
            }
            // The label notes (after the title) read the legend labels in order.
            var labels = drawing.Notes.Skip(1).Select(n => n.Text).ToArray();
            Assert.Equal(legend.Entries.Select(e => e.Label).ToArray(), labels);
        }

        [Fact]
        public void LegendView_RowsDescendDownThePage()
        {
            var drawing = DmxWireLegendPlanner.Build(DmxWireLegend.Build(new[] { DmxWireType.Lv(2), DmxWireType.Lv(6) }));
            for (int i = 1; i < drawing.Markers.Count; i++)
                Assert.True(drawing.Markers[i].Position.Y < drawing.Markers[i - 1].Position.Y);
        }

        // ── Helpers (mirrors DmxOneLinePlannerTests) ────────────────────────────────────────────────────
        private const double V = 24.0;

        private static DmxContract Contract() => new DmxContract(
            decoderPool: new[] { DecoderSpec.Dmx4_5000_10A, DecoderSpec.Dmx6_22K },
            driverPool: new[] { new DriverType("320", 320.0, V, 0.0), new DriverType("600", 600.0, V, 0.0) },
            systemVolts: V, channelCeiling: 512, reservedChannels: 0, maxDevicesPerSegment: 32,
            breakerAmps: 20, feedVolts: 120, breakerContinuousDerate: 0.8, maxDriversPerBreaker: 0);

        private static ZoneDesign Zone(string name, int channels) =>
            new ZoneDesign(name, new[] { new TapeRun(94.3, 5.2, channels) });   // ~490 W ⇒ one decoder
    }
}
