#nullable enable
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;

namespace TurboSuite.Dali
{
    /// <summary>
    /// Turns the job's declared DALI loops into a <see cref="ControlSubsystemDemand"/>: the recommended
    /// <c>LQSE2-1DALUNV-D</c> module count and the QS-link device/leg budget the DALI subsystem consumes.
    /// The third concrete subsystem after DMX and shades — pure, so the shim only reads the model, counts
    /// the loads per loop, and hands over the per-loop tallies.
    ///
    /// <b>The grain (module <c>LQSE2-1DALUNV-D</c> NA, 1 DALI bus per module):</b>
    /// <list type="bullet">
    ///   <item><b>module count = loop count</b> — one module per declared loop, because a module carries
    ///   exactly one bus. There is no auto-splitting; a loop bigger than a bus is the designer's to split
    ///   (see the cap warning), not the solver's.</item>
    ///   <item><b>LinkDevices = module count.</b> The module is the QS device; its DALI loads live on the
    ///   DALI bus <i>downstream</i> of it and are NOT QS devices. This is the key difference from shades,
    ///   where each shade is itself a QS device — here the loads count for legs only.</item>
    ///   <item><b>LinkLoads = total DALI loads.</b> Each addressable load = 1 switch leg (a fully loaded
    ///   64-load bus is 1 device / 64 legs — leg-heavy, device-light).</item>
    ///   <item><b>Mount <see cref="DemandMount.DinSlot"/>, PDU 0.</b> The module drops into a panel
    ///   module slot alongside the dimming modules (MUX/MUX/COM, no V+ ⇒ draws no processor PDU).</item>
    /// </list>
    ///
    /// <b>The 64-loads-per-bus cap is a warning, not a hard stop.</b> A single DALI bus addresses at most
    /// 64 loads; a loop declared past that can't physically fit on its one module, but the fix is a
    /// geometry call — split the zones into more loops — so the solver reports the demand it was given and
    /// flags the over-cap loops rather than silently inventing extra modules. Mirrors the DMX pattern of a
    /// bill that stands alongside a caveat.
    /// </summary>
    public static class DaliSolver
    {
        /// <summary>DALI-addressable loads a single bus (one module) can carry. Over is a warning.</summary>
        public const int MaxLoadsPerBus = 64;

        /// <summary>The Lutron HomeWorks DALI-2 Power Module, NA/QSX, one bus. Ordered on the BOM, one per
        /// declared loop, dropped into a panel DIN slot.</summary>
        public const string ModulePartNumber = "LQSE2-1DALUNV-D";

        /// <summary>BOM description carried on the demanded part, so the line reads correctly without a
        /// BrandConfig catalog entry (the module is allocator-supplied, never hand-placed like the DMX
        /// interface, so there is no placed line for it to match).</summary>
        public const string ModuleDescription = "GEN2 HW Universal ESN DALI 1-LINK";

        /// <summary>The subsystem name the BOM and the panel breakdown label this demand with.</summary>
        public const string SubsystemName = "DALI";

        public static ControlSubsystemDemand Solve(IReadOnlyList<DaliLoopTally>? loops)
        {
            if (loops == null || loops.Count == 0)
                return ControlSubsystemDemand.None(SubsystemName);

            // A loop with no loads is a declared bus controlling nothing — it orders no module (a BOM line
            // must be orderable). Drop it silently; if that leaves nothing, the DALI subsystem is a clean
            // nothing, not a problem to report.
            var active = loops.Where(l => l.LoadCount > 0).ToList();
            if (active.Count == 0)
                return ControlSubsystemDemand.None(SubsystemName);

            int moduleCount = active.Count;                 // 1 bus/module ⇒ module count = loop count
            int totalLoads = active.Sum(l => l.LoadCount);  // each load = 1 switch leg

            // Over-cap loops: can't fit on their one bus. Warn (still solve) — the split is the designer's.
            var overCap = active.Where(l => l.LoadCount > MaxLoadsPerBus).ToList();
            string? diagnostic = overCap.Count == 0 ? null : DescribeOverCap(overCap);

            return new ControlSubsystemDemand(
                SubsystemName,
                parts: new List<DemandPart>
                {
                    new DemandPart(ModulePartNumber, moduleCount, DemandMount.DinSlot, ModuleDescription)
                },
                linkDevices: moduleCount,
                linkLoads: totalLoads,
                diagnostic: diagnostic);
        }

        private static string DescribeOverCap(IReadOnlyList<DaliLoopTally> overCap)
        {
            var loops = string.Join("; ", overCap.Select(l => $"\"{l.LoopName}\" ({l.LoadCount})"));
            return overCap.Count == 1
                ? $"loop {loops} over {MaxLoadsPerBus} loads/bus — split it into more loops."
                : $"{overCap.Count} loops over {MaxLoadsPerBus} loads/bus ({loops}) — split each up.";
        }
    }
}
