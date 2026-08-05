using System.Collections.Generic;

namespace TurboSuite.Shared.Services;

/// <summary>
/// The BAND_ROOM tiebreak, extracted Revit-free so it can be pinned by unit tests.
///
/// Context: room detection neutralizes a fixture's height (it probes each room at that room's own
/// bounding-box mid-Z, not the fixture's real Z) because architects leave room upper limits at an
/// arbitrary default, so ceiling-hosted fixtures sit above the room volume and match nothing. That
/// makes the fixture match every stacked storey in the same plan column, so ~two-thirds of fixtures
/// come back with 2+ candidate rooms. This picks the right one.
///
/// A room's <b>top</b> is fiction (the architect's limit offset); a room's <b>bottom</b> is real —
/// it is the storey's floor slab. So reconstruct each room's effective vertical band as "its own floor
/// up to the floor of the next room above it in the same column," and hand the fixture to the room whose
/// band contains it. Equivalently: <b>among the plan matches, take the one with the highest floor at or
/// below the fixture.</b> Uses no levels and no host↔link level correspondence — only floor elevations,
/// which both documents agree on.
/// </summary>
public static class RoomBandSelector
{
    /// <summary>At-or-below tolerance (ft). A floor within EPS above the fixture still counts as below it.</summary>
    public const double DefaultEpsilon = 0.01;

    /// <summary>
    /// Given the host-coordinate floor elevation of each plan-matched candidate room and the fixture's
    /// host-coordinate Z, returns the index of the winning candidate, or -1 if there are no candidates.
    ///
    /// Winner = highest floor at or below the fixture (within <paramref name="epsilon"/>). If the fixture
    /// sits below <i>every</i> candidate floor (slab-recessed, or exterior grade), fall back to the lowest
    /// floor. Ties in floor elevation resolve to the <b>earliest</b> candidate, so callers preserve the
    /// host-before-link ordering by adding host-doc rooms to the list first.
    /// </summary>
    public static int SelectBandIndex(IReadOnlyList<double> candidateFloorZs, double fixtureZ,
        double epsilon = DefaultEpsilon)
    {
        if (candidateFloorZs == null || candidateFloorZs.Count == 0)
            return -1;

        // Highest floor at or below the fixture. Strict '>' means an equal floor does not displace an
        // earlier candidate, so the first-added (host) room wins a tie.
        int best = -1;
        for (int i = 0; i < candidateFloorZs.Count; i++)
        {
            double floor = candidateFloorZs[i];
            if (floor <= fixtureZ + epsilon && (best == -1 || floor > candidateFloorZs[best]))
                best = i;
        }
        if (best != -1)
            return best;

        // Below every candidate floor — take the lowest floor (again, earliest wins a tie).
        best = 0;
        for (int i = 1; i < candidateFloorZs.Count; i++)
            if (candidateFloorZs[i] < candidateFloorZs[best])
                best = i;
        return best;
    }
}
