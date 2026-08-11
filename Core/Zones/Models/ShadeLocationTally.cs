#nullable enable
namespace TurboSuite.Zones.Models
{
    /// <summary>One shade location — the panel the designer assigned shades to (e.g. "SHADE 1") — and
    /// how many shade motors are circuited there. The shim groups shade circuits into these by location,
    /// exactly as lighting circuits group into zones; <see cref="Services.ShadeSolver"/> then recommends
    /// the QSPS-10PNL count per location. The name is for grouping/diagnostics; the order is a count.</summary>
    public sealed class ShadeLocationTally
    {
        public ShadeLocationTally(string locationName, int shadeCount)
        {
            LocationName = locationName;
            ShadeCount = shadeCount;
        }

        public string LocationName { get; }
        public int ShadeCount { get; }
    }
}
