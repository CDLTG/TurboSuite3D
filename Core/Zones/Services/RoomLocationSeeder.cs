using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Zones.Services
{
    /// <summary>How a room's location was derived from its control signals.</summary>
    public enum RoomLocationSeedKind
    {
        /// <summary>No non-zero vote from either signal — a locally-switched non-Lutron room,
        /// or a DALI room not yet zoned in TurboDALI. No seed; falls to manual.</summary>
        None,

        /// <summary>Exactly one distinct location across both signals — auto-seed it.</summary>
        Unanimous,

        /// <summary>More than one distinct location — don't guess; flag for the designer.
        /// On real jobs this is usually a name collision (two Spaces sharing a name in
        /// different locations, merged by the name key) or a stray fixture from an unbuilt
        /// Space, not a genuine multi-location room.</summary>
        Mixed,
    }

    /// <summary>
    /// The classified location for one room, with the raw candidates kept for the
    /// mixed-flag histogram hint the sidebar shows.
    /// </summary>
    public readonly struct RoomLocationSeed
    {
        public RoomLocationSeedKind Kind { get; }

        /// <summary>The seeded location when <see cref="Kind"/> is
        /// <see cref="RoomLocationSeedKind.Unanimous"/>; otherwise 0.</summary>
        public int Location { get; }

        /// <summary>Distinct non-zero location votes, ascending (one entry when unanimous,
        /// several when mixed, empty when none).</summary>
        public IReadOnlyList<int> Candidates { get; }

        /// <summary>Vote count per candidate location — the mixed-flag histogram hint.</summary>
        public IReadOnlyDictionary<int, int> Histogram { get; }

        internal RoomLocationSeed(RoomLocationSeedKind kind, int location,
            IReadOnlyList<int> candidates, IReadOnlyDictionary<int, int> histogram)
        {
            Kind = kind;
            Location = location;
            Candidates = candidates;
            Histogram = histogram;
        }
    }

    /// <summary>
    /// Pure room→location derivation (Phase A "auto-seed"). A room's location is derived,
    /// not hand-entered, from two signals in the <b>same integer namespace</b>:
    /// <list type="number">
    /// <item><b>paneled lighting</b> — the room's lighting-circuit panel names →
    /// <see cref="PanelAllocationService.ParseLocationNumber"/> (the name-prefix, e.g. "1-A" → 1);</item>
    /// <item><b>DALI lighting</b> — the room's DALI fixtures' "Control Zone" values → the declared
    /// loop whose <c>ZoneValues</c> contains the value → that loop's <c>AssignedZone</c> (already a
    /// location number). Needed because DALI fixtures are panel-less, so signal (1) misses them.</item>
    /// </list>
    /// The votes are classified by their distinct non-zero values: one → unanimous (auto-seed),
    /// more than one → mixed (defer, don't guess), none → no seed. Only explicit designer picks
    /// persist; auto-seeds re-derive each session (self-healing).
    /// </summary>
    public static class RoomLocationSeeder
    {
        /// <summary>Paneled-lighting votes: each panel name parsed to its location number
        /// (unparseable / location-0 names contribute no vote). See
        /// <see cref="PanelAllocationService.ParseLocationNumber"/>.</summary>
        public static IEnumerable<int> PanelVotes(IEnumerable<string> panelNames)
        {
            if (panelNames == null) yield break;
            foreach (var name in panelNames)
            {
                int loc = PanelAllocationService.ParseLocationNumber(name);
                if (loc > 0) yield return loc;
            }
        }

        /// <summary>
        /// DALI votes: for each of a room's "Control Zone" values, the declared loop whose
        /// <c>ZoneValues</c> contains that value contributes its <c>AssignedZone</c> (0 = not yet
        /// zoned → no vote). Takes loops as plain tuples so Core/Zones stays free of the DALI
        /// persistence DTO; the shim adapts <c>DaliLoopDto</c> at the collection seam.
        /// </summary>
        public static IEnumerable<int> DaliVotes(
            IEnumerable<string> roomControlZoneValues,
            IReadOnlyList<(IReadOnlyList<string> ZoneValues, int AssignedZone)> loops)
        {
            if (roomControlZoneValues == null || loops == null) yield break;
            foreach (var value in roomControlZoneValues)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                foreach (var loop in loops)
                {
                    if (loop.AssignedZone <= 0 || loop.ZoneValues == null) continue;
                    if (loop.ZoneValues.Any(z => string.Equals(z, value, System.StringComparison.OrdinalIgnoreCase)))
                    {
                        yield return loop.AssignedZone;
                        break; // one vote per value; first owning loop wins
                    }
                }
            }
        }

        /// <summary>Classify a room from both signals' votes (convenience over
        /// <see cref="Classify(IEnumerable{int})"/>).</summary>
        public static RoomLocationSeed Classify(IEnumerable<int> panelVotes, IEnumerable<int> daliVotes)
            => Classify((panelVotes ?? Enumerable.Empty<int>()).Concat(daliVotes ?? Enumerable.Empty<int>()));

        /// <summary>Classify a room from a combined multiset of location votes. Non-positive
        /// votes are ignored; classification is on the distinct non-zero values.</summary>
        public static RoomLocationSeed Classify(IEnumerable<int> votes)
        {
            var histogram = new Dictionary<int, int>();
            if (votes != null)
            {
                foreach (var v in votes)
                {
                    if (v <= 0) continue;
                    histogram[v] = histogram.TryGetValue(v, out int n) ? n + 1 : 1;
                }
            }

            var candidates = histogram.Keys.OrderBy(k => k).ToList();
            if (candidates.Count == 0)
                return new RoomLocationSeed(RoomLocationSeedKind.None, 0, candidates, histogram);
            if (candidates.Count == 1)
                return new RoomLocationSeed(RoomLocationSeedKind.Unanimous, candidates[0], candidates, histogram);
            return new RoomLocationSeed(RoomLocationSeedKind.Mixed, 0, candidates, histogram);
        }

        /// <summary>
        /// The per-room resolution ladder: an explicit designer pick (a positive location)
        /// always wins; otherwise a unanimous auto-seed; otherwise 0 (blank — a valid state).
        /// A non-positive <paramref name="explicitPick"/> means "no pick."
        /// </summary>
        public static int Resolve(int explicitPick, in RoomLocationSeed seed)
        {
            if (explicitPick > 0) return explicitPick;
            if (seed.Kind == RoomLocationSeedKind.Unanimous) return seed.Location;
            return 0;
        }
    }
}
