#nullable disable
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Dali.Services;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Services;

namespace TurboSuite.Dali.Services
{
    /// <summary>
    /// Shim-side <see cref="IDaliAddressWriter"/> — writes the reconciler's <c>unitKey → "L2-00"</c> map to the
    /// <b>"DALI Address" param on each unit's one live element</b>: the driver device (driver unit) or the
    /// self-driven fixture (downlight unit), resolved through the shared <see cref="DaliUnitEnumerator"/> (so
    /// the write targets match the addressing read exactly). It then <b>clears</b> any bound fixture/device
    /// carrying a value it did not write this pass — including a driver's tape fixtures, which no longer carry
    /// the address — so no orphan label lingers. Idempotent: an element already carrying its value is left
    /// alone, so an unchanged model writes nothing. One transaction. Runs inside the work queue (Revit API thread).
    ///
    /// <para><b>Worksharing:</b> the writes are planned before anything changes, and exactly the elements whose
    /// value will change (stamped or cleared) are leased per operation. A refused lease or a rolled-back commit
    /// writes nothing and returns null, which the ViewModel treats as "not saved" (no lock is captured).</para>
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

            // 1. Each addressed unit's element takes its label; only a differing value is an actual write.
            var pending = new Dictionary<ElementId, string>();
            var addressed = new HashSet<ElementId>();
            int addressedCount = 0;
            foreach (var kv in addressByUnit)
            {
                if (!targetByKey.TryGetValue(kv.Key, out var id)) continue;   // unit no longer in the model
                var p = WritableAddress(_doc.GetElement(id));
                if (p == null) continue;                                       // unbound elements are a no-op
                addressed.Add(id);
                addressedCount++;
                if (p.AsString() != kv.Value) pending[id] = kv.Value;
            }

            // 2. Stale labels — any bound fixture/device with a value we didn't just address.
            int cleared = 0;
            foreach (var id in StaleIds(addressed))
            {
                pending[id] = "";
                cleared++;
            }

            if (pending.Count > 0)
            {
                var lease = WorksharingLease.Acquire(_doc, pending.Keys, "DALI device");
                if (!lease.IsGranted)
                {
                    TaskDialog.Show("TurboDALI", lease.DenialMessage);
                    return null;
                }

                using (var tx = new Transaction(_doc, "TurboDALI — write addresses"))
                {
                    tx.Start();
                    foreach (var kv in pending)
                        WritableAddress(_doc.GetElement(kv.Key))?.Set(kv.Value);
                    if (!CommitGuard.TryCommit(tx))
                    {
                        TaskDialog.Show("TurboDALI", WorksharingMessages.RolledBack);
                        return null;
                    }
                }
            }

            return $"Wrote {addressedCount} address{(addressedCount == 1 ? "" : "es")}"
                 + (cleared > 0 ? $"; cleared {cleared} stale" : "") + ".";
        }

        /// <summary>Every bound Lighting Fixture / Lighting Device that carries a DALI Address but is not in
        /// <paramref name="addressed"/> — spanning both categories.</summary>
        private List<ElementId> StaleIds(HashSet<ElementId> addressed)
        {
            var stale = new List<ElementId>();
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
                    if (addressed.Contains(el.Id)) continue;
                    var p = WritableAddress(el);
                    if (p == null || string.IsNullOrEmpty(p.AsString())) continue;   // unbound or already blank
                    stale.Add(el.Id);
                }
            }
            return stale;
        }

        private static Parameter WritableAddress(Element el)
        {
            var p = el?.LookupParameter(ParameterNames.DaliAddress);
            return p == null || p.IsReadOnly || p.StorageType != StorageType.String ? null : p;
        }
    }
}
