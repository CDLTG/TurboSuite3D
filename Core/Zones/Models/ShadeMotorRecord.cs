#nullable enable
namespace TurboSuite.Zones.Models
{
    /// <summary>
    /// One shade motor, as the control one-line's motor list draws it (Phase E) — a shade circuit's
    /// number and its load name (e.g. <c>M03 · shade motor 'a'</c>). By convention a shade circuit is
    /// one output = one motor, so a record is one circuit; a circuit carrying several motors repeats the
    /// record so the list row count matches the QSPS-10PNL's motor fill. The per-motor records ride
    /// alongside the shade panel's motor <i>count</i>: the count drives capacity, the records drive the
    /// list — the same split as <see cref="KeypadRecord"/>.
    /// </summary>
    public sealed class ShadeMotorRecord
    {
        /// <summary>The shade circuit number (the row's identity).</summary>
        public string Circuit { get; }

        /// <summary>The circuit's load name — the row's descriptor (no separate room field).</summary>
        public string LoadName { get; }

        public ShadeMotorRecord(string circuit, string loadName)
        {
            Circuit = circuit ?? string.Empty;
            LoadName = loadName ?? string.Empty;
        }
    }
}
