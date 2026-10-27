#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using TurboSuite.Abstractions;
using TurboSuite.Driver.Models;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Services;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Shim-side <see cref="IRpsRevitOperations"/> — binds the active document and runs every
    /// call on the Revit API thread (via the work queue). Supersedes
    /// <c>ElementUpdateService</c> for the RPS path: the in-place swap is the same
    /// <c>device.Symbol = newType</c> body, but batched into ONE transaction so a whole
    /// "Re-run selected" is a single undo step.
    ///
    /// <para><b>Worksharing:</b> no session lease — circuits and drivers are hot, so each write borrows just its
    /// own elements (<see cref="WorksharingLease.Acquire"/>) and runs nothing unless all of them are held.</para>
    /// </summary>
    public class RpsRevitOperations : IRpsRevitOperations
    {
        private readonly Document _doc;
        private readonly UIDocument _uidoc;

        public RpsRevitOperations(UIDocument uidoc)
        {
            _uidoc = uidoc;
            _doc = uidoc.Document;
        }

        public bool SelectInProject(ElementRef circuitRef)
        {
            var circuitId = circuitRef.ToElementId();
            if (_doc.GetElement(circuitId) is not ElectricalSystem circuit)
                return false;

            // Select the circuit's member elements (fixtures + supplies), matching the
            // manual workflow — not just the circuit object itself.
            var ids = new List<ElementId>();
            if (circuit.Elements != null)
            {
                foreach (Element el in circuit.Elements)
                    ids.Add(el.Id);
            }

            if (ids.Count == 0)
                ids.Add(circuitId);

            _uidoc.Selection.SetElementIds(ids);
            _uidoc.ShowElements(ids);
            return true;
        }

        public int? SwapDriverTypes(IReadOnlyList<DriverSwap> swaps)
        {
            if (swaps == null || swaps.Count == 0)
                return 0;

            int swapped = 0;

            // The retyped devices, plus any target type that has to be activated first.
            var workingSet = swaps.Select(s => s.DeviceRef.ToElementId())
                .Concat(swaps.Select(s => s.NewTypeRef).Distinct()
                    .Select(r => _doc.GetElement(r.ToElementId()))
                    .OfType<FamilySymbol>().Where(sym => !sym.IsActive).Select(sym => sym.Id));
            if (!Lease(workingSet, "power supply")) return null;

            using (var trans = new Transaction(_doc, "TurboRPS — Re-run drivers"))
            {
                trans.Start();

                // Activate each distinct target symbol once before assignment.
                var targetIds = swaps.Select(s => s.NewTypeRef).Distinct();
                bool anyActivated = false;
                foreach (var typeRef in targetIds)
                {
                    if (_doc.GetElement(typeRef.ToElementId()) is FamilySymbol sym && !sym.IsActive)
                    {
                        sym.Activate();
                        anyActivated = true;
                    }
                }
                if (anyActivated)
                    _doc.Regenerate();

                foreach (var swap in swaps)
                {
                    try
                    {
                        if (_doc.GetElement(swap.DeviceRef.ToElementId()) is not FamilyInstance device)
                            continue;
                        if (_doc.GetElement(swap.NewTypeRef.ToElementId()) is not FamilySymbol newType)
                            continue;
                        if (device.Symbol?.Id == newType.Id)
                        {
                            swapped++;
                            continue;
                        }

                        device.Symbol = newType;
                        swapped++;
                    }
                    catch
                    {
                        // Skip a device that can't be retyped (e.g. unexpected cross-family);
                        // the rest still apply within this single transaction.
                    }
                }

                if (!CommitOrReport(trans)) return null;
            }

            return swapped;
        }

        public IReadOnlyList<RpsCircuitData> Rescan()
        {
            return RpsCircuitDataBuilder.Build(_doc);
        }

        public bool? SetDeferred(ElementRef circuitRef, bool deferred, string signature)
        {
            if (_doc.GetElement(circuitRef.ToElementId()) is not ElectricalSystem circuit)
                return false;

            // The deferral lives in ExtensibleStorage on the circuit element itself.
            if (!Lease(new[] { circuit.Id }, "circuit")) return null;
            if (!RpsDeferralStorageService.Save(_doc, circuit, deferred, signature))
            {
                TaskDialog.Show("TurboRPS", WorksharingMessages.RolledBack);
                return null;
            }
            return true;
        }

        private bool Lease(IEnumerable<ElementId> workingSet, string itemNoun)
        {
            var lease = WorksharingLease.Acquire(_doc, workingSet, itemNoun);
            if (lease.IsGranted) return true;
            TaskDialog.Show("TurboRPS", lease.DenialMessage);
            return false;
        }

        private static bool CommitOrReport(Transaction tx)
        {
            if (CommitGuard.TryCommit(tx)) return true;
            TaskDialog.Show("TurboRPS", WorksharingMessages.RolledBack);
            return false;
        }
    }
}
