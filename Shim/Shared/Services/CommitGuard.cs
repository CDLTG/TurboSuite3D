#nullable disable
using Autodesk.Revit.DB;

namespace TurboSuite.Shared.Services
{
    /// <summary>
    /// Honor <see cref="Transaction.Commit"/>'s status. Under worksharing a write to an element another user
    /// owns does not throw and <c>Parameter.Set</c> still returns <c>true</c> — the denial surfaces only at
    /// commit, which comes back <see cref="TransactionStatus.RolledBack"/> (spike-confirmed, Revit 2026). A
    /// caller that ignores the status reports a save that Revit discarded.
    ///
    /// <para>This is the backstop, not the primary mechanism: <see cref="WorksharingLease"/> borrows the
    /// working set before anything is written, so a rollback here means Revit borrowed something implicitly
    /// that the lease didn't name. Revit's own failure dialog (which names the owner) is left to show; callers
    /// surface <see cref="WorksharingMessages.RolledBack"/> and mark nothing saved.</para>
    /// </summary>
    public static class CommitGuard
    {
        /// <summary>Commit and report whether the changes actually landed.</summary>
        public static bool TryCommit(Transaction tx) => tx.Commit() == TransactionStatus.Committed;
    }
}
