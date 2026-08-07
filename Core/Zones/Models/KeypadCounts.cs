#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Zones.Models
{
    /// <summary>
    /// The job's keypads, split the two ways the control link cares about.
    ///
    /// Gang and radio are independent questions with different consequences: gang decides how many
    /// devices a keypad counts as, radio decides <b>which link</b> those devices land on. A wired
    /// keypad consumes a QS link's device budget; a wireless one consumes a Clear Connect link's. So
    /// they are counted apart rather than summed — a single "keypads" number cannot say where they go.
    /// </summary>
    public sealed class KeypadCounts
    {
        /// <summary>Single-gang wired keypads — one QS device each.</summary>
        public int Regular { get; set; }

        /// <summary>Two-gang wired keypads — two QS devices each.</summary>
        public int TwoGang { get; set; }

        /// <summary>Wireless keypads, <b>already expanded to device count</b> (a two-gang wireless
        /// keypad contributes 2). Flattened here because nothing downstream needs to tell a wireless
        /// two-gang from two wireless singles.</summary>
        public int WirelessDevices { get; set; }

        /// <summary>
        /// The same keypads again, counted for the BOM instead of the link — grouped by catalog
        /// number rather than by gang and radio.
        ///
        /// Two views of one walk, not two sources: the counts above answer "how much link capacity
        /// does this consume", these answer "what do we order", and neither is derivable from the
        /// other. Gang doubles a device but not an order line; radio decides which link but not which
        /// part. Produced together so they cannot disagree about the job.
        /// </summary>
        public IReadOnlyList<ControlDeviceTally> Tallies { get; set; } = new List<ControlDeviceTally>();
    }
}
