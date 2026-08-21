#nullable enable
namespace TurboSuite.Dali.Services
{
    /// <summary>Revit-free contract for the addressing read — the shim enumerates the model's addressable DALI
    /// units and returns a <see cref="DaliModelSnapshot"/> (per-unit key + zone + kind + ordinal + circuit
    /// centroid). Invoked through the work queue so the read runs on the Revit API thread. Shares the one
    /// unit enumeration with <c>DaliDemandProvider</c>'s count read, so demand and addressing can't diverge;
    /// this one keeps the identity + geometry the ordering needs.</summary>
    public interface IDaliModelReader
    {
        DaliModelSnapshot Read();
    }
}
