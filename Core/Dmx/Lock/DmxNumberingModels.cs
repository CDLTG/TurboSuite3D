#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx.Lock
{
    // Pure, Revit-free types for the Phase-3 numbering lock (§8c). The reconciler turns a solved bill (as a
    // canonical-order list of solved zones) + the frozen lock baseline into a concrete DEC-number assignment
    // plus any REVIEW verdicts, all without touching Revit so it's fully unit-testable.

    /// <summary>The one solved zone the numbering cares about: its addressing interface, decoder type, and
    /// how many decoders fell out of the pack — extracted from the bill in canonical (bill-walk) order.</summary>
    public sealed class DmxSolvedZone
    {
        public DmxSolvedZone(string zoneValue, int interfaceNumber, string decoderType, int decoderCount)
        {
            ZoneValue = zoneValue;
            InterfaceNumber = interfaceNumber;
            DecoderType = decoderType;
            DecoderCount = decoderCount;
        }

        public string ZoneValue { get; }
        public int InterfaceNumber { get; }
        public string DecoderType { get; }
        public int DecoderCount { get; }
    }

    /// <summary>One zone's assigned DEC numbers after reconciliation, in pack order.</summary>
    public sealed class DmxZoneNumbering
    {
        public DmxZoneNumbering(string zoneValue, int interfaceNumber, string decoderType, IReadOnlyList<int> decIds)
        {
            ZoneValue = zoneValue;
            InterfaceNumber = interfaceNumber;
            DecoderType = decoderType;
            DecIds = decIds;
        }

        public string ZoneValue { get; }
        public int InterfaceNumber { get; }
        public string DecoderType { get; }
        public IReadOnlyList<int> DecIds { get; }
    }

    /// <summary>A lock-aware verdict: a locked-zone change that would mislabel an issued DEC # (§8c REVIEW).</summary>
    public sealed class DmxReviewItem
    {
        public DmxReviewItem(string zoneValue, string message)
        {
            ZoneValue = zoneValue;
            Message = message;
        }

        public string ZoneValue { get; }
        public string Message { get; }
    }

    /// <summary>The complete numbering for one solve: every zone's DEC #s (in canonical order) + any REVIEWs.</summary>
    public sealed class DmxNumbering
    {
        public DmxNumbering(IReadOnlyList<DmxZoneNumbering> zones, IReadOnlyList<DmxReviewItem> reviews)
        {
            Zones = zones;
            Reviews = reviews;
            DecIdsByZone = zones.ToDictionary(z => z.ZoneValue, z => z.DecIds);
        }

        public IReadOnlyList<DmxZoneNumbering> Zones { get; }
        public IReadOnlyList<DmxReviewItem> Reviews { get; }

        /// <summary>Per-zone DEC #s, for the placement planner to stamp Switch IDs in pack order.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<int>> DecIdsByZone { get; }

        public bool HasReviews => Reviews.Count > 0;
    }
}
