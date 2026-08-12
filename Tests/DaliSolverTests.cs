using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dali;
using TurboSuite.Zones.Models;
using Xunit;

namespace TurboSuite.Tests.Dali
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for DaliSolver (Core/Dali/DaliSolver.cs).
    //
    //  DALI is the third control subsystem (after DMX and shades). Its grain is settled by the
    //  LQSE2-1DALUNV-D (NA, 1 bus/module): module count = loop count, and the module is the ONLY QS device
    //  (its loads live on the DALI bus downstream, counting for legs only). So the two edges worth pinning:
    //    • LinkDevices = module count (NOT loads + modules like shades) — a load is a leg, not a device.
    //    • the 64-loads-per-bus cap is a warning that rides alongside the demand, never an auto-split.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    public class DaliSolverTests
    {
        private static DaliLoopTally Loop(int loads, string name = "Loop A")
            => new DaliLoopTally(name, loads);

        private static ControlSubsystemDemand Solve(params DaliLoopTally[] loops)
            => DaliSolver.Solve(loops.ToList());

        [Fact]
        public void NoLoops_IsACleanNothing()
        {
            var d = DaliSolver.Solve(new List<DaliLoopTally>());
            Assert.Empty(d.Parts);
            Assert.Equal(0, d.LinkDevices);
            Assert.Equal(0, d.LinkLoads);
            Assert.False(d.HasDiagnostic);
        }

        [Fact]
        public void NullLoops_DoesNotThrow()
            => Assert.Empty(DaliSolver.Solve(null).Parts);

        [Fact]
        public void LoopsWithNoLoads_IsACleanNothing()
        {
            var d = Solve(Loop(0, "Empty A"), Loop(0, "Empty B"));
            Assert.Empty(d.Parts);
            Assert.False(d.HasDiagnostic);   // silent — an empty declared bus is not a problem to report
        }

        /// <summary>One loop with loads is one module: 1 device (the module), legs = its loads. This is the
        /// DALI signature — the loads are NOT devices, unlike shades.</summary>
        [Fact]
        public void OneLoop_IsOneModule_OneDeviceLoadsAsLegs()
        {
            var d = Solve(Loop(12));

            var part = Assert.Single(d.Parts);
            Assert.Equal("LQSE2-1DALUNV-D", part.PartNumber);
            Assert.Equal(1, part.Quantity);
            Assert.Equal(DemandMount.DinSlot, part.Mount);   // competes for a panel module slot
            Assert.Equal(1, d.LinkDevices);                  // the module only
            Assert.Equal(12, d.LinkLoads);                   // 12 addressable loads = 12 legs
            Assert.False(d.HasDiagnostic);
        }

        /// <summary>Module count = loop count, and legs are the sum across loops. Three loops of 10/20/5:
        /// 3 modules, 3 devices, 35 legs.</summary>
        [Fact]
        public void ModuleCountEqualsLoopCount_LegsAreSummed()
        {
            var d = Solve(Loop(10, "A"), Loop(20, "B"), Loop(5, "C"));

            Assert.Equal(3, Assert.Single(d.Parts).Quantity);
            Assert.Equal(3, d.LinkDevices);
            Assert.Equal(35, d.LinkLoads);
        }

        /// <summary>A fully loaded bus is the leg-heavy, device-light extreme the record predicted:
        /// 64 loads on one module → 1 device, 64 legs, no warning (64 is the cap, not over it).</summary>
        [Fact]
        public void FullBus_IsOneDeviceSixtyFourLegs_NoWarning()
        {
            var d = Solve(Loop(64));

            Assert.Equal(1, d.LinkDevices);
            Assert.Equal(64, d.LinkLoads);
            Assert.False(d.HasDiagnostic);
        }

        /// <summary>Empty loops mixed with real ones drop out silently — they order no module and don't
        /// warn. Two real loops (8, 3) + an empty one: 2 modules, 11 legs.</summary>
        [Fact]
        public void EmptyLoopsDropOut_RealOnesStand()
        {
            var d = Solve(Loop(8, "A"), Loop(0, "Empty"), Loop(3, "B"));

            Assert.Equal(2, Assert.Single(d.Parts).Quantity);
            Assert.Equal(2, d.LinkDevices);
            Assert.Equal(11, d.LinkLoads);
            Assert.False(d.HasDiagnostic);
        }

        /// <summary>Over 64 on one bus warns but STILL solves — no auto-split into a second module. The
        /// demand carries the real leg count (70) so the link's 512-leg budget still sees the truth, and
        /// the diagnostic names the loop so the designer can split it.</summary>
        [Fact]
        public void OverCapLoop_WarnsButStillReportsOneModuleAndAllLegs()
        {
            var d = Solve(Loop(70, "North Bus"));

            Assert.Equal(1, Assert.Single(d.Parts).Quantity);   // one module, NOT ceil(70/64)=2
            Assert.Equal(1, d.LinkDevices);
            Assert.Equal(70, d.LinkLoads);                      // the real legs, for the 512 cap
            Assert.True(d.HasDiagnostic);
            Assert.Contains("North Bus", d.Diagnostic);
            Assert.Contains("64", d.Diagnostic);
        }

        /// <summary>Multiple over-cap loops are batched into one diagnostic, and the solve still stands for
        /// every loop (3 modules here — two over-cap, one fine).</summary>
        [Fact]
        public void MultipleOverCapLoops_AreBatchedAndStillSolved()
        {
            var d = Solve(Loop(80, "A"), Loop(90, "B"), Loop(10, "C"));

            Assert.Equal(3, Assert.Single(d.Parts).Quantity);
            Assert.Equal(3, d.LinkDevices);
            Assert.Equal(180, d.LinkLoads);
            Assert.True(d.HasDiagnostic);
            Assert.Contains("A", d.Diagnostic);
            Assert.Contains("B", d.Diagnostic);
        }
    }
}
