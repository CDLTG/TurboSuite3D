#nullable enable
using System.Collections.Generic;
using TurboSuite.Dali.Addressing;

namespace TurboSuite.Dali.Services
{
    /// <summary>The Revit-free read of the model the addressing engine needs — the identity-preserving
    /// sibling read (plan H1) produced shim-side by <see cref="IDaliModelReader"/> and fed to
    /// <see cref="DaliAddressReconciler"/>. One <see cref="DaliCircuitReading"/> per DALI circuit (its key,
    /// zone, and fixture centroid); the write-back element set stays shim-side (Core never holds an
    /// ElementId).</summary>
    public sealed class DaliModelSnapshot
    {
        public DaliModelSnapshot(IReadOnlyList<DaliCircuitReading> circuits, int uncircuitedDaliCount)
        {
            Circuits = circuits;
            UncircuitedDaliCount = uncircuitedDaliCount;
        }

        /// <summary>Every DALI circuit in the model, keyed by <c>circuit.UniqueId</c>.</summary>
        public IReadOnlyList<DaliCircuitReading> Circuits { get; }

        /// <summary>DALI fixtures sitting on no circuit — warned-and-excluded (H2), never addressed. Surfaced
        /// so the window can nudge the designer to circuit them (the "hardware present but undeclared" voice).</summary>
        public int UncircuitedDaliCount { get; }
    }
}
