#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.Exceptions;

namespace TurboSuite.Shared.Services
{
    /// <summary>Outcome of <see cref="WorksharingLease.Acquire"/>. <see cref="DenialMessage"/> is null when granted.</summary>
    public sealed class LeaseResult
    {
        public static readonly LeaseResult Granted = new LeaseResult(true, null);

        public bool IsGranted { get; }
        public string DenialMessage { get; }

        private LeaseResult(bool granted, string message)
        {
            IsGranted = granted;
            DenialMessage = message;
        }

        public static LeaseResult Denied(string message) => new LeaseResult(false, message);
    }

    /// <summary>
    /// Lock up front, never collide. Before an operation writes, it borrows its whole working set from central;
    /// it runs only if every element is held, and runs nothing otherwise — no partial writes, no per-element
    /// denial handling. On a non-workshared document every call is granted immediately and touches nothing.
    ///
    /// <para>Spike-confirmed behavior this rests on (Revit 2026, live cloud model):</para>
    /// <list type="bullet">
    /// <item><see cref="WorksharingUtils.CheckoutElements(Document, ICollection{ElementId})"/> on ids a teammate
    /// owns returns a short set and doesn't throw, so it is safe to run speculatively. Ids already held by the
    /// current user come back in the result, so granted = result ⊇ working set.</item>
    /// <item>It also refuses an element whose central copy is newer than local, so holding the lease <i>is</i> the
    /// freshness check.</item>
    /// <item>ExtensibleStorage <see cref="DataStorage"/> elements behave exactly like model elements.</item>
    /// </list>
    ///
    /// <para><b>Acquire-only — there is no release.</b> Sync with Central is the only release point; TurboSuite
    /// never calls <c>RelinquishOwnership</c>. It is document-wide (no per-element overload) and frees elements
    /// that carry the user's own finalized, unsynced edits — calling it would hand a teammate the exact collision
    /// this exists to prevent. So a refused lease keeps whatever it partially took until the next sync, and a
    /// granted one holds its set until then too, even after the window closes.</para>
    /// </summary>
    public static class WorksharingLease
    {
        /// <summary>
        /// Borrow <paramref name="workingSet"/> for one operation. Call with no transaction open, before writing.
        /// </summary>
        /// <param name="itemNoun">Singular noun the denial message counts with ("lighting type", "circuit"…).</param>
        public static LeaseResult Acquire(Document doc, IEnumerable<ElementId> workingSet, string itemNoun = "item") =>
            AcquireCore(doc, workingSet, null, null, itemNoun);

        /// <summary>
        /// Borrow a modeless module's session working set: its <see cref="ModuleLockStorageService"/> lock store
        /// plus <paramref name="workingSet"/>. The lock store is created on first use (its own committed
        /// transaction — so the calling command must return <c>Result.Succeeded</c> to keep it). Call with no
        /// transaction open, before the window opens.
        /// </summary>
        public static LeaseResult AcquireSession(Document doc, string module, IEnumerable<ElementId> workingSet,
            string itemNoun = "item")
        {
            if (doc == null || !doc.IsWorkshared) return LeaseResult.Granted;

            List<ElementId> lockIds;
            try { lockIds = ModuleLockStorageService.EnsureLockIds(doc, module); }
            catch (Exception ex) { return LeaseResult.Denied($"Couldn't set up the {module} lock:\n{ex.Message}"); }
            if (lockIds.Count == 0)
                return LeaseResult.Denied($"Couldn't set up the {module} lock. {WorksharingMessages.RolledBack}");

            return AcquireCore(doc, workingSet, lockIds, module, itemNoun);
        }

        private static LeaseResult AcquireCore(Document doc, IEnumerable<ElementId> workingSet,
            IReadOnlyCollection<ElementId> lockIds, string module, string itemNoun)
        {
            if (doc == null || !doc.IsWorkshared) return LeaseResult.Granted;

            var wanted = new HashSet<ElementId>(
                (workingSet ?? Enumerable.Empty<ElementId>()).Where(id => id != null && id != ElementId.InvalidElementId));
            if (lockIds != null) wanted.UnionWith(lockIds);
            if (wanted.Count == 0) return LeaseResult.Granted;

            ICollection<ElementId> held;
            try
            {
                held = WorksharingUtils.CheckoutElements(doc, wanted.ToList());
            }
            catch (CentralModelException)
            {
                return LeaseResult.Denied(WorksharingMessages.Unreachable);
            }
            catch (Exception ex)
            {
                return LeaseResult.Denied($"Couldn't borrow from the central model:\n{ex.Message}");
            }

            var heldSet = new HashSet<ElementId>(held ?? new List<ElementId>());
            var shortfall = wanted.Where(id => !heldSet.Contains(id)).ToList();
            if (shortfall.Count == 0) return LeaseResult.Granted;

            // Classify the shortfall. Partial borrows are kept (released at the next sync) — see the class doc.
            var lockSet = lockIds != null ? new HashSet<ElementId>(lockIds) : new HashSet<ElementId>();
            var lockOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var itemsByOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int stale = 0;

            foreach (var id in shortfall)
            {
                string owner = null;
                CheckoutStatus status;
                try { status = WorksharingUtils.GetCheckoutStatus(doc, id, out owner); }
                catch { status = CheckoutStatus.NotOwned; }

                if (status == CheckoutStatus.OwnedByOtherUser && !string.IsNullOrWhiteSpace(owner))
                {
                    if (lockSet.Contains(id)) lockOwners.Add(owner);
                    else itemsByOwner[owner] = (itemsByOwner.TryGetValue(owner, out var n) ? n : 0) + 1;
                }
                else
                {
                    // No owner but still refused: the central copy is newer than local.
                    stale++;
                }
            }

            return LeaseResult.Denied(WorksharingMessages.BuildDenial(module, lockOwners, itemsByOwner, stale, itemNoun));
        }
    }
}
