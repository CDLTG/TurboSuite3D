#nullable enable
namespace TurboSuite.Zones.Models
{
    /// <summary>
    /// One DALI module to place into a zone's panel — a declared loop that the designer assigned to this
    /// ZONE N. The TurboZones shim builds a <c>zone → list of these</c> map from the persisted loops (declared
    /// in TurboDALI) and their required zone assignment, and hands it to
    /// <see cref="Services.PanelAllocationService.BuildPanelBreakdown"/>.
    ///
    /// <b>This drives placement only, never the order.</b> The DALI module count and the QS-link budget
    /// come from the job-wide <c>DaliSolver</c> demand (the single BOM/link authority); this type just
    /// tells the allocator which panel a module occupies a slot in, so the placed slot is deliberately
    /// tagged BOM/link-excluded (<see cref="ModuleResult.OrderedBySubsystem"/>). The name labels the slot
    /// (a DALI module carries a bus/loop, not circuits); the load count is carried for the panel schedule.
    /// </summary>
    public sealed class DaliPanelModule
    {
        public DaliPanelModule(string loopName, int loadCount)
        {
            LoopName = loopName;
            LoadCount = loadCount;
        }

        public string LoopName { get; }
        public int LoadCount { get; }
    }
}
