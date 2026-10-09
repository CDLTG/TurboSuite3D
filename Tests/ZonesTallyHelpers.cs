using System.Collections.Generic;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;

namespace TurboSuite.Tests.Zones
{
    /// <summary>
    /// Builds <see cref="ControlDeviceTally"/> lists for the Zones suites.
    ///
    /// Repeaters used to be a bare int on BomExtras. They are now counted from the model by catalog
    /// number, and the int is derived from that — one count for both the order line and the Clear
    /// Connect link math, rather than two fields that could drift. These helpers keep the tests
    /// reading like the old ones did while going through the real shape.
    /// </summary>
    internal static class Tally
    {
        /// <summary>A single-model repeater fleet: <paramref name="count"/> devices, all one catalog
        /// number, which is what a normal job looks like. Device count and order quantity coincide
        /// here precisely because the type declares one part — see
        /// <see cref="ControlDeviceGroup.DeviceCount"/> for why they are separate fields anyway.</summary>
        public static ControlDeviceGroup Repeaters(int count, string catalog = "HQR-REP-120")
            => new ControlDeviceGroup
            {
                DeviceCount = count,
                Tallies = count <= 0 ? new List<ControlDeviceTally>() : Of((catalog, count))
            };

        /// <summary>A repeater-location tally for the per-location Clear Connect sizing tests:
        /// <paramref name="repeaters"/> repeaters at <paramref name="locationName"/> (e.g. "1-REP1",
        /// whose <c>ParseLocationNumber</c> reads 1), with any wireless keypads circuited there. The
        /// location number is parsed the same way the shim provider does, so the tests exercise the
        /// real <see cref="RepeaterLocationTally.Location"/> field the packer groups on.</summary>
        public static RepeaterLocationTally Loc(
            string locationName, int repeaters, params KeypadRecord[] wirelessKeypads)
        {
            int loc = PanelAllocationService.ParseLocationNumber(locationName);
            // Build one RepeaterRecord per repeater; the keypads ride the FIRST record (the flat totals the
            // sizing tests assert are the same whichever repeater holds them). A keypads-but-no-repeater
            // edge still keeps a record so nothing is lost.
            var recs = new List<RepeaterRecord>();
            for (int i = 0; i < repeaters; i++)
                recs.Add(new RepeaterRecord($"{locationName}#{i + 1}", loc,
                    i == 0 ? (IReadOnlyList<KeypadRecord>)wirelessKeypads : System.Array.Empty<KeypadRecord>()));
            if (repeaters == 0 && wirelessKeypads.Length > 0)
                recs.Add(new RepeaterRecord(locationName, loc, wirelessKeypads));
            return new RepeaterLocationTally(locationName, loc, recs);
        }

        /// <summary>A wireless keypad record at <paramref name="location"/>, <paramref name="devices"/>
        /// device weight (2 = two-gang).</summary>
        public static KeypadRecord Keypad(string switchId, int location, int devices = 1)
            => new KeypadRecord(switchId, room: "", model: "", location, devices);

        /// <summary>A repeater fleet whose order rows deliberately do not match its device count —
        /// the shape that catches anyone summing parts to size a link.</summary>
        public static ControlDeviceGroup RepeaterGroup(
            int deviceCount, params (string Catalog, int Qty)[] rows)
            => new ControlDeviceGroup { DeviceCount = deviceCount, Tallies = Of(rows) };

        /// <summary>Arbitrary rows. A null or empty catalog number is the "type carries none" case.</summary>
        public static IReadOnlyList<ControlDeviceTally> Of(params (string Catalog, int Qty)[] rows)
        {
            var list = new List<ControlDeviceTally>();
            foreach (var (catalog, qty) in rows)
            {
                list.Add(new ControlDeviceTally
                {
                    CatalogNumber = catalog ?? "",
                    TypeName = string.IsNullOrEmpty(catalog) ? "Unnamed Type" : catalog,
                    Quantity = qty
                });
            }
            return list;
        }

        /// <summary>Rows that carry a type name distinct from the catalog number — for the
        /// missing-catalog cases, where the type name is the only thing identifying the offender.</summary>
        public static IReadOnlyList<ControlDeviceTally> Named(
            params (string? Catalog, string TypeName, int Qty)[] rows)
        {
            var list = new List<ControlDeviceTally>();
            foreach (var (catalog, typeName, qty) in rows)
            {
                list.Add(new ControlDeviceTally
                {
                    CatalogNumber = catalog ?? "",
                    TypeName = typeName,
                    Quantity = qty
                });
            }
            return list;
        }

        /// <summary>Rows carrying the description the family supplied for that slot.</summary>
        public static IReadOnlyList<ControlDeviceTally> Described(
            params (string Catalog, string Description, int Qty)[] rows)
        {
            var list = new List<ControlDeviceTally>();
            foreach (var (catalog, description, qty) in rows)
            {
                list.Add(new ControlDeviceTally
                {
                    CatalogNumber = catalog ?? "",
                    TypeName = catalog ?? "Unnamed Type",
                    Description = description ?? "",
                    Quantity = qty
                });
            }
            return list;
        }
    }
}
