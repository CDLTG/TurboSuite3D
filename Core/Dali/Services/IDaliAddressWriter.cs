#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Dali.Services
{
    /// <summary>
    /// Revit-free contract for DALI address write-back. Given the reconciler's
    /// <c>circuit.UniqueId → "L2-01"</c> map, the shim writes that label to the <b>"DALI Address" param on
    /// every element of each addressed circuit</b> — its tape/downlight fixtures AND the remote driver/decoder
    /// device (both categories carry the param) — and <b>clears</b> any bound element that is no longer on an
    /// addressed circuit, so the model never accumulates orphan labels. Idempotent, one transaction; invoked
    /// through the work queue so it runs on the Revit API thread.
    /// </summary>
    public interface IDaliAddressWriter
    {
        /// <summary>Write each circuit's address to all its elements and clear stale ones. Returns a short
        /// status (e.g. "Wrote 34 addresses on 41 elements; cleared 3").</summary>
        string Write(IReadOnlyDictionary<string, string> addressByCircuit);
    }
}
