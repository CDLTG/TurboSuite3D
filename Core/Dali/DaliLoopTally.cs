#nullable enable
namespace TurboSuite.Dali
{
    /// <summary>
    /// One declared DALI loop reduced to what the solver needs: its name (for diagnostics) and the number
    /// of DALI-addressable loads on it. The shim builds these from the persisted loops
    /// (<see cref="TurboSuite.Dali.Input.DaliStateMapper"/>) by counting the DALI fixtures across each
    /// loop's Control Zones — one addressable load per fixture — exactly as shade circuits reduce to a
    /// <see cref="TurboSuite.Zones.Models.ShadeLocationTally"/>.
    ///
    /// The load count is the load-bearing number here (pun intended): each load is one switch leg, so a
    /// loop's loads are what pressure the link's 512-leg budget and what the 64-loads-per-bus cap checks.
    /// </summary>
    public sealed class DaliLoopTally
    {
        public DaliLoopTally(string loopName, int loadCount)
        {
            LoopName = loopName;
            LoadCount = loadCount;
        }

        public string LoopName { get; }

        /// <summary>DALI-addressable loads on this loop = switch legs it consumes. 0 ⇒ a declared bus with
        /// nothing on it, which orders no module.</summary>
        public int LoadCount { get; }
    }
}
