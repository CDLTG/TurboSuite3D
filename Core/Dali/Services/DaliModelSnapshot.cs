#nullable enable
using System.Collections.Generic;
using TurboSuite.Dali.Input;

namespace TurboSuite.Dali.Services
{
    /// <summary>The Revit-free read of the model the addressing engine needs — produced shim-side by
    /// <see cref="IDaliModelReader"/> and fed to <c>DaliAddressReconciler</c>. One
    /// <see cref="DaliUnitReading"/> per <b>circuited</b> addressable unit (a driver device or a self-driven
    /// downlight); the write-back element set stays shim-side (Core never holds an ElementId).</summary>
    public sealed class DaliModelSnapshot
    {
        public DaliModelSnapshot(
            IReadOnlyList<DaliUnitReading> units,
            int uncircuitedDaliCount,
            IReadOnlyList<string>? warnings = null)
        {
            Units = units;
            UncircuitedDaliCount = uncircuitedDaliCount;
            Warnings = warnings ?? new List<string>();
        }

        /// <summary>Every circuited addressable DALI unit in the model, keyed by its durable unit key.</summary>
        public IReadOnlyList<DaliUnitReading> Units { get; }

        /// <summary>DALI fixtures sitting on no circuit — warned-and-excluded, never addressed. Surfaced
        /// so the window can nudge the designer to circuit them (the "hardware present but undeclared" voice).</summary>
        public int UncircuitedDaliCount { get; }

        /// <summary>Non-fatal read warnings (e.g. a circuit whose drivers share a Switch ID suffix, so their
        /// addresses were assigned but aren't redeploy-stable) — surfaced alongside the REVIEWs.</summary>
        public IReadOnlyList<string> Warnings { get; }
    }
}
