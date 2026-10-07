#nullable enable
using System;
using System.Collections.Generic;

namespace TurboSuite.Zones.Models
{
    /// <summary>One shade location — the panel the designer assigned shades to (e.g. "SHADE 1") — and
    /// how many shade motors are circuited there. The shim groups shade circuits into these by location,
    /// exactly as lighting circuits group into zones; <see cref="Services.ShadeSolver"/> then recommends
    /// the QSPS-10PNL count per location. The name is for grouping/diagnostics; the order is a count.
    ///
    /// <para><see cref="Motors"/> carries the location's per-motor records (Phase E) in circuit order, so
    /// the solver can slice them into each QSPS-10PNL for the one-line's motor list. It is additive — the
    /// panel math reads <see cref="ShadeCount"/>, not this — so an empty list just means no per-motor
    /// list is drawn (the legacy <c>n MOTORS</c> stub).</para></summary>
    public sealed class ShadeLocationTally
    {
        public ShadeLocationTally(string locationName, int shadeCount,
            IReadOnlyList<ShadeMotorRecord>? motors = null)
        {
            LocationName = locationName;
            ShadeCount = shadeCount;
            Motors = motors ?? Array.Empty<ShadeMotorRecord>();
        }

        public string LocationName { get; }
        public int ShadeCount { get; }

        /// <summary>Per-motor records for this location, in circuit order (count aligns with
        /// <see cref="ShadeCount"/>). Empty ⇒ the motor list falls back to the count stub.</summary>
        public IReadOnlyList<ShadeMotorRecord> Motors { get; }
    }
}
