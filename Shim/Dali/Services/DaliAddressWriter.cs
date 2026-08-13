#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Dali.Services;
using TurboSuite.Shared.Constants;

namespace TurboSuite.Dali.Services
{
    /// <summary>
    /// Shim-side <see cref="IDaliAddressWriter"/> — writes the reconciler's <c>circuit.UniqueId → "L2-01"</c>
    /// map to the <b>"DALI Address" param on every element of each addressed circuit</b> (fixtures AND the
    /// remote driver/decoder device — H10), then <b>clears</b> any bound element no longer on an addressed
    /// circuit so no orphan label lingers (H8). Idempotent (an unchanged model re-writes the same values),
    /// one transaction. Runs inside the work queue (Revit API thread), so it opens its own transaction like
    /// the other DALI writes.
    /// </summary>
    public sealed class DaliAddressWriter : IDaliAddressWriter
    {
        private readonly Document _doc;

        public DaliAddressWriter(Document doc) => _doc = doc;

        public string Write(IReadOnlyDictionary<string, string> addressByCircuit)
        {
            addressByCircuit ??= new Dictionary<string, string>();
            var written = new HashSet<ElementId>();
            int writtenCount = 0;

            using (var tx = new Transaction(_doc, "TurboDALI — write addresses"))
            {
                tx.Start();

                // 1. Stamp each addressed circuit's elements (both categories) with its label.
                var circuits = new FilteredElementCollector(_doc)
                    .OfClass(typeof(ElectricalSystem))
                    .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
                    .Cast<ElectricalSystem>();

                foreach (var circuit in circuits)
                {
                    if (!addressByCircuit.TryGetValue(circuit.UniqueId, out string address)) continue;

                    foreach (Element el in circuit.Elements)
                    {
                        if (!TrySet(el, address)) continue;   // null-guarded: unbound elements are a no-op
                        written.Add(el.Id);
                        writtenCount++;
                    }
                }

                // 2. Clear stale labels — any bound fixture/device with a value we didn't just write.
                int cleared = ClearStale(written);

                tx.Commit();

                return $"Wrote {writtenCount} address{(writtenCount == 1 ? "" : "es")} on "
                     + $"{addressByCircuit.Count} circuit{(addressByCircuit.Count == 1 ? "" : "s")}"
                     + (cleared > 0 ? $"; cleared {cleared} stale" : "") + ".";
            }
        }

        /// <summary>Blank the DALI Address on every bound Lighting Fixture / Lighting Device that carries a
        /// value but was not written this pass — spanning both categories (H8).</summary>
        private int ClearStale(HashSet<ElementId> written)
        {
            int cleared = 0;
            var cats = new[] { BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingDevices };
            foreach (var cat in cats)
            {
                var elements = new FilteredElementCollector(_doc)
                    .OfCategory(cat)
                    .WhereElementIsNotElementType()
                    .OfClass(typeof(FamilyInstance))
                    .ToElements();

                foreach (var el in elements)
                {
                    if (written.Contains(el.Id)) continue;
                    var p = el.LookupParameter(ParameterNames.DaliAddress);
                    if (p == null || p.IsReadOnly || p.StorageType != StorageType.String) continue;
                    if (string.IsNullOrEmpty(p.AsString())) continue;   // already blank
                    p.Set("");
                    cleared++;
                }
            }
            return cleared;
        }

        private static bool TrySet(Element el, string address)
        {
            var p = el.LookupParameter(ParameterNames.DaliAddress);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.String) return false;
            p.Set(address);
            return true;
        }
    }
}
