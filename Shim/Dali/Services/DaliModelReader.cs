#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Dali.Input;
using TurboSuite.Dali.Services;

namespace TurboSuite.Dali.Services
{
    /// <summary>
    /// Shim-side <see cref="IDaliModelReader"/> — the addressing read. Delegates to the shared
    /// <see cref="DaliUnitEnumerator"/> and projects it into a <see cref="DaliModelSnapshot"/>: the
    /// <b>circuited</b> addressable units (a circuit anchors an address; an uncircuited fixture can't be
    /// addressed yet), the count of uncircuited DALI fixtures (the "circuit them" nudge), and any read
    /// warnings (e.g. drivers sharing a Switch ID suffix).
    ///
    /// This is the identity-preserving sibling of <c>DaliDemandProvider.CountDaliLoadsByZone</c>: both ride the
    /// one enumeration, so the demand count and the issued addresses can't disagree.
    /// </summary>
    public sealed class DaliModelReader : IDaliModelReader
    {
        private readonly Document _doc;

        public DaliModelReader(Document doc) => _doc = doc;

        public DaliModelSnapshot Read()
        {
            var scan = DaliUnitEnumerator.Scan(_doc);

            var circuited = new List<DaliUnitReading>();
            int uncircuited = 0;
            foreach (var e in scan.Entries)
            {
                if (e.Reading.CircuitKey.Length > 0) circuited.Add(e.Reading);
                else uncircuited++;   // an uncircuited DALI fixture — warned-and-excluded from addressing
            }

            return new DaliModelSnapshot(circuited, uncircuited, scan.Warnings);
        }
    }
}
