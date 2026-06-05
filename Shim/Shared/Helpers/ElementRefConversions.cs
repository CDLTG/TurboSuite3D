using Autodesk.Revit.DB;
using TurboSuite.Abstractions;

namespace TurboSuite.Shared.Helpers
{
    /// <summary>
    /// Boundary conversions between Revit's <see cref="ElementId"/> and the
    /// Revit-free <see cref="ElementRef"/> carried by Core DTOs.
    ///
    /// THIS is the version-specific seam. Revit 2025+: <c>ElementId.Value</c> is a
    /// long and <c>new ElementId(long)</c> exists. The 2024 shim's copy of this file
    /// will use <c>ElementId.IntegerValue</c> (int) and <c>new ElementId(int)</c>
    /// instead — the only place that divergence needs to live.
    /// </summary>
    internal static class ElementRefConversions
    {
        public static ElementRef ToRef(this ElementId? id) =>
            id == null ? ElementRef.None : new ElementRef(id.Value);

        public static ElementId ToElementId(this ElementRef r) => new ElementId(r.Value);
    }
}
