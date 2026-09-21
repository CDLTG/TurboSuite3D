#nullable enable
namespace TurboSuite.Zones.Models
{
    /// <summary>
    /// What kind of work a control-link unit is, for the convention-driven arrangement (rule #2) and
    /// the one-line renderer (Section 2). The packer's fan-out keeps categories apart where a spare QS
    /// link allows it, keypads highest isolation priority; the categories are otherwise packing-neutral
    /// — the count/fit/BOM never depend on them.
    /// </summary>
    public enum LinkCategory
    {
        /// <summary>Not tagged — the default a bare unit carries when nothing set a category.</summary>
        None = 0,

        /// <summary>A dimmer panel's modules — the located, indivisible bulk of a QS link.</summary>
        Modules,

        /// <summary>A Sivoia QS shade panel (QSPS-10PNL) — located and indivisible like a dimmer panel,
        /// its shade motors a fill inside it rather than link units of their own.</summary>
        Shades,

        /// <summary>Keypads — the location-less aggregate that pours into leftover QS capacity, isolated
        /// onto a spare QS link where one exists (never a Clear Connect link).</summary>
        Keypads,

        /// <summary>A DMX/DALI interface (QSE-CI-DMX, DALI DIN module) — sited units inherit their host
        /// panel's location; a floating one is location-less.</summary>
        Interface
    }
}
