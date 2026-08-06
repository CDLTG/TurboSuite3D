#nullable enable
using System;
using System.Collections.Generic;
using TurboSuite.Dmx.Persistence;

namespace TurboSuite.Dmx.Input
{
    /// <summary>
    /// TurboDMX's solve, run from the persisted design alone — no window, no live edits.
    ///
    /// This is what lets the control BOM order QSE-CI-DMX interfaces from real channel math instead of
    /// a hand-picked dropdown, and it re-solves rather than reading a stored count so the number
    /// tracks the model. The window's own <c>Run()</c> stays separate on purpose: it solves the
    /// designer's <i>in-progress</i> edits, which is a different question from "what does the saved
    /// job need". Both go through <see cref="DmxStateMapper"/>, so they cannot disagree about what the
    /// saved job says.
    ///
    /// <b>Never throws.</b> Every refusal the engine can raise comes back as
    /// <see cref="DmxHeadlessResult.Diagnostic"/>. A purchasing document must still build when DMX is
    /// half-declared — the failure mode to avoid is not a wrong BOM, it is no BOM at all.
    /// </summary>
    public static class DmxHeadlessSolve
    {
        public static DmxHeadlessResult Solve(DmxModelSnapshot? snapshot, DmxModuleState? state)
        {
            snapshot ??= new DmxModelSnapshot();
            state ??= new DmxModuleState();
            var settingsDto = state.Settings;

            try
            {
                var zoneResult = DmxZoneBuilder.Build(snapshot.Fixtures, state.Clusters);

                // No DMX in this job at all. A clean nothing, not a problem to report: most jobs have
                // no DMX, and a BOM warning on every one of them would be noise.
                if (zoneResult.Zones.Count == 0)
                    return DmxHeadlessResult.Nothing();

                // From here the job HAS DMX, so anything that stops the solve is worth saying out loud
                // — there is real hardware that will not make it onto the order.
                var decoders = DmxStateMapper.ToCuratedDecoders(snapshot.DecoderCandidates, settingsDto);
                if (decoders.Count == 0)
                    return DmxHeadlessResult.Blocked(
                        "DMX zones exist but no decoder type is selected in TurboDMX — open TurboDMX "
                        + "and pick the job's decoder kit.", zoneResult.ZoneNames);

                var drivers = DmxStateMapper.ToCuratedDrivers(snapshot.DriverCandidates, settingsDto);
                if (drivers.Count == 0)
                    return DmxHeadlessResult.Blocked(
                        "DMX zones exist but no driver type is selected in TurboDMX — open TurboDMX "
                        + "and pick the job's driver kit.", zoneResult.ZoneNames);

                var contract = DmxContractBuilder.Build(
                    DmxStateMapper.ToProfile(settingsDto),
                    DmxStateMapper.ToJobSettings(settingsDto),
                    decoders, drivers);

                var loops = DmxStateMapper.ToLoopDeclarations(state.Loops, zoneResult.ZoneNames);

                var bill = DmxSolver.Solve(contract, zoneResult.Zones, loops);
                return DmxHeadlessResult.Solved(bill, zoneResult.ZoneNames);
            }
            // The engine's pre-solve hard stops are the designer's to fix in TurboDMX, and they carry
            // their own batched messages — pass them straight through.
            catch (UnmappableTapeException ex) { return DmxHeadlessResult.Blocked(ex.Message); }
            catch (OverCapRunsException ex) { return DmxHeadlessResult.Blocked(ex.Message); }
            catch (OverCapLoopsException ex) { return DmxHeadlessResult.Blocked(ex.Message); }
            catch (LoopDeclarationException ex) { return DmxHeadlessResult.Blocked(ex.Message); }
            // Catch-all, matching the window's: the engine's other refusals (a mixed-channel zone, a
            // breaker cap too small for a driver) plus anything unforeseen. Nothing from a DMX design
            // may take down the document that was being generated.
            catch (Exception ex) { return DmxHeadlessResult.Blocked(ex.Message); }
        }
    }

    /// <summary>The outcome of a headless solve: a bill, or a reason there isn't one.</summary>
    public sealed class DmxHeadlessResult
    {
        private DmxHeadlessResult(DmxBill? bill, IReadOnlyList<string> zoneNames, string? diagnostic)
        {
            Bill = bill;
            ZoneNames = zoneNames;
            Diagnostic = diagnostic;
        }

        /// <summary>The solved bill, or null when the job has no DMX or the solve was blocked.</summary>
        public DmxBill? Bill { get; }

        /// <summary>The control zones found, carried through even when the solve was blocked — the
        /// designer's zone names are still the most useful thing to show next to the reason.</summary>
        public IReadOnlyList<string> ZoneNames { get; }

        /// <summary>Why there is no bill, in the engine's own words. Null when the job simply has no
        /// DMX — that case is silent by design.</summary>
        public string? Diagnostic { get; }

        internal static DmxHeadlessResult Solved(DmxBill bill, IReadOnlyList<string> zoneNames) =>
            new DmxHeadlessResult(bill, zoneNames, null);

        internal static DmxHeadlessResult Nothing() =>
            new DmxHeadlessResult(null, new List<string>(), null);

        internal static DmxHeadlessResult Blocked(string diagnostic, IReadOnlyList<string>? zoneNames = null) =>
            new DmxHeadlessResult(null, zoneNames ?? new List<string>(), diagnostic);
    }
}
