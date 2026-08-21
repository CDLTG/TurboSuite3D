#nullable disable
using System.Collections.Generic;
using Autodesk.Revit.DB;
using TurboSuite.Dali.Services;
using TurboSuite.Shared.Constants;

namespace TurboSuite.Dali.Services
{
    /// <summary>
    /// Shim-side <see cref="IDaliAddressWriter"/> — writes the reconciler's <c>unitKey → "L2-00"</c> map to the
    /// <b>"DALI Address" param on each unit's one live element</b>: the driver device (driver unit) or the
    /// self-driven fixture (downlight unit), resolved through the shared <see cref="DaliUnitEnumerator"/> (so
    /// the write targets match the addressing read exactly). It then <b>clears</b> any bound fixture/device
    /// carrying a value it did not write this pass — including a driver's tape fixtures, which no longer carry
    /// the address — so no orphan label lingers. Idempotent (an unchanged model re-writes the same values),
    /// one transaction. Runs inside the work queue (Revit API thread).
    /// </summary>
    public sealed class DaliAddressWriter : IDaliAddressWriter
    {
        private readonly Document _doc;

        public DaliAddressWriter(Document doc) => _doc = doc;

        public string Write(IReadOnlyDictionary<string, string> addressByUnit)
        {
            addressByUnit ??= new Dictionary<string, string>();

            // Resolve each unit key to its live write target (same enumeration the read used).
            var targetByKey = new Dictionary<string, ElementId>();
            foreach (var e in DaliUnitEnumerator.Scan(_doc).Entries)
                targetByKey[e.Reading.UnitKey] = e.WriteTargetId;

            var written = new HashSet<ElementId>();
            int writtenCount = 0;

            using (var tx = new Transaction(_doc, "TurboDALI — write addresses"))
            {
                tx.Start();

                // 1. Stamp each addressed unit's element with its label.
                foreach (var kv in addressByUnit)
                {
                    if (!targetByKey.TryGetValue(kv.Key, out var id)) continue;   // unit no longer in the model
                    var el = _doc.GetElement(id);
                    if (el == null || !TrySet(el, kv.Value)) continue;            // unbound elements are a no-op
                    written.Add(el.Id);
                    writtenCount++;
                }

                // 2. Clear stale labels — any bound fixture/device with a value we didn't just write.
                int cleared = ClearStale(written);

                tx.Commit();

                return $"Wrote {writtenCount} address{(writtenCount == 1 ? "" : "es")}"
                     + (cleared > 0 ? $"; cleared {cleared} stale" : "") + ".";
            }
        }

        /// <summary>Blank the DALI Address on every bound Lighting Fixture / Lighting Device that carries a
        /// value but was not written this pass — spanning both categories.</summary>
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
