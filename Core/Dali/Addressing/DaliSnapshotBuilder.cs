#nullable enable
using System.Linq;
using TurboSuite.Dali.Persistence;

namespace TurboSuite.Dali.Addressing
{
    /// <summary>Captures a resolved <see cref="DaliAddressing"/> as the frozen lock baseline (Lock / Re-lock
    /// event) — the DALI analog of <c>DmxSnapshotBuilder.Capture</c>, two-level (loops + circuits). The live
    /// spatial-walk order is deliberately NOT stored; only the issued numbers are, so a later re-walk can
    /// churn freely while locked yet never move an already-issued <c>L{loop}-{load##}</c>.</summary>
    public static class DaliSnapshotBuilder
    {
        public static DaliSnapshotDto Capture(DaliAddressing addressing, string state = "Locked")
        {
            return new DaliSnapshotDto
            {
                NumberingState = state,
                Loops = addressing.LoopNumbers
                    .Select(kv => new DaliSnapshotLoopDto { LoopId = kv.Key, LoopNumber = kv.Value })
                    .OrderBy(l => l.LoopNumber)
                    .ToList(),
                Circuits = addressing.Addresses
                    .Select(a => new DaliSnapshotCircuitDto
                    {
                        CircuitKey = a.CircuitKey,
                        LoopId = a.LoopId,
                        LoopNumber = a.LoopNumber,
                        LoadNumber = a.LoadNumber,
                        Zone = a.Zone,
                    })
                    .ToList(),
            };
        }
    }
}
