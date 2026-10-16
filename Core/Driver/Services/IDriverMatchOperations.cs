#nullable disable
using TurboSuite.Driver.Models;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Revit-free seam for the TurboDriver match-check window. The shim implementation re-collects the
    /// project's driver and RPS-fixture types; the ViewModel invokes it through the work queue so the
    /// collection runs on Revit's API thread (the Reload button). The comparison itself is pure Core.
    /// </summary>
    public interface IDriverMatchOperations
    {
        MatchCatalog Collect();
    }
}
