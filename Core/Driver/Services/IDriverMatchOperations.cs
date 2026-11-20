#nullable disable
using TurboSuite.Abstractions;
using TurboSuite.Driver.Models;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Revit-free seam for the TurboRPS Driver Match tab. The shim implementation re-collects the
    /// project's driver families, RPS-fixture types and links, and writes links; the ViewModel invokes
    /// it through the work queue so it runs on Revit's API thread. The comparison itself is pure Core.
    /// </summary>
    public interface IDriverMatchOperations
    {
        MatchCatalog Collect();

        /// <summary>Link a fixture type to a driver family (replacing any existing link for that
        /// type). True on success; null when refused (worksharing) or rolled back — already reported.</summary>
        bool? SetLink(ElementRef fixtureTypeRef, ElementRef driverFamilyRef);

        /// <summary>Remove the link stored under <paramref name="fixtureTypeKey"/> (works for broken
        /// links, whose fixture type may be gone). True on success; null when refused / rolled back.</summary>
        bool? RemoveLink(string fixtureTypeKey);
    }
}
