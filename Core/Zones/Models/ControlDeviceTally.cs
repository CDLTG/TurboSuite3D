#nullable enable

namespace TurboSuite.Zones.Models
{
    /// <summary>
    /// One orderable line of a modelled control device — keypads, hybrid repeaters — counted from the
    /// job and identified by a catalog number its family type carries.
    ///
    /// The control BOM used to print these as the literal words "Keypad" and "Two-Gang Keypad" with no
    /// part number at all: a quantity on a purchasing document with nothing to order against. Now the
    /// numbers come off the type, and lines merge by them — so two keypad families sharing a model
    /// collapse to one line, and one family with two models does not.
    ///
    /// <b>One type produces several of these.</b> A keypad is a base unit plus button kits plus a
    /// faceplate, so each of the type's six catalog slots yields a row, quantities resolved by that
    /// slot's <c>Catalog Qty</c> rule. A row is therefore a part, never a device — see
    /// <see cref="ControlDeviceGroup"/> for why the link math cannot be summed out of these.
    ///
    /// A blank <see cref="CatalogNumber"/> is a real state, not an error to swallow: the devices are
    /// placed and must still be counted. <see cref="TypeName"/> exists to name the offender on the
    /// design surface so it can be fixed.
    /// </summary>
    public sealed class ControlDeviceTally
    {
        /// <summary>The slot's catalog number, trimmed. Empty only when the type declared none at all.</summary>
        public string CatalogNumber { get; set; } = string.Empty;

        /// <summary>The family type this came from — used to name a type whose catalog number is
        /// missing, or whose quantity rule would not parse. Types that share a catalog number merge,
        /// and the survivor's name is arbitrary.</summary>
        public string TypeName { get; set; } = string.Empty;

        public int Quantity { get; set; }

        /// <summary>
        /// What the part is, in words. Empty when the type says nothing about it.
        ///
        /// A family carries one description per <i>type</i>, not one per catalog slot, so the two are
        /// paired by position: <c>Catalog Number1</c> takes the built-in Description,
        /// <c>Catalog Number2</c> takes <c>Description2</c>. Slots 3–6 have no field left to draw on
        /// and stay blank — which is honest rather than lossy, since nothing in the library uses them
        /// yet. If they ever do, that is the point to decide between more parameters and a lookup
        /// table, not before.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>Why this row's quantity should not be trusted — an unparseable
        /// <c>Catalog Qty</c> token, in the parser's own words. Null on a clean read. Surfaced on the
        /// design surface only: a purchasing document is not where a family gets fixed.</summary>
        public string? Diagnostic { get; set; }

        public bool HasCatalogNumber => !string.IsNullOrEmpty(CatalogNumber);
        public bool HasDiagnostic => !string.IsNullOrWhiteSpace(Diagnostic);
    }
}
