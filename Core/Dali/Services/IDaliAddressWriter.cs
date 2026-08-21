#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Dali.Services
{
    /// <summary>
    /// Revit-free contract for DALI address write-back. Given the reconciler's <c>unitKey → "L2-00"</c> map,
    /// the shim resolves each unit key to its <b>one live element</b> — the driver device (driver unit) or the
    /// self-driven fixture (downlight unit) — and writes the label to that element's "DALI Address" param,
    /// then <b>clears</b> any bound fixture/device carrying a value it did not write this pass (including a
    /// driver's tape fixtures, which no longer carry the address), so the model never accumulates orphan
    /// labels. Idempotent, one transaction; invoked through the work queue so it runs on the Revit API thread.
    /// </summary>
    public interface IDaliAddressWriter
    {
        /// <summary>Write each unit's address to its resolved element and clear stale ones. Returns a short
        /// status (e.g. "Wrote 34 addresses; cleared 3 stale.").</summary>
        string Write(IReadOnlyDictionary<string, string> addressByUnit);
    }
}
