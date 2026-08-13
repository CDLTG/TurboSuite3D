#nullable enable
namespace TurboSuite.Dali.Services
{
    /// <summary>Revit-free contract for the addressing read — the shim walks the model's DALI circuits and
    /// returns a <see cref="DaliModelSnapshot"/> (per-circuit key + zone + fixture centroid). Invoked through
    /// the work queue so the read runs on the Revit API thread. Distinct from <c>DaliDemandProvider</c>'s
    /// count read: this one preserves circuit identity + geometry the ordering needs (plan H1).</summary>
    public interface IDaliModelReader
    {
        DaliModelSnapshot Read();
    }
}
