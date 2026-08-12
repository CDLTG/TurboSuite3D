using System.Collections.Generic;
using TurboSuite.Zones.Models;

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
