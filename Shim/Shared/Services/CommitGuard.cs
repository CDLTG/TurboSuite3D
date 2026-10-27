#nullable disable
using System;
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

        /// <summary>
        /// Commit, throwing <see cref="WorksharingException"/> when Revit rolled it back. For shared helpers
        /// that run inside a <b>synchronous</b> command (TurboDriver, TurboWire): the command catches it and
        /// returns <c>Result.Failed</c>, which discards every commit of the run — so a rollback midway leaves
        /// nothing half-done. Call it outside any <c>try/catch</c> that would swallow it.
        /// </summary>
        public static void CommitOrThrow(Transaction tx)
        {
            if (!TryCommit(tx)) throw new WorksharingException(WorksharingMessages.RolledBack);
        }
    }

    /// <summary>
    /// A worksharing refusal or rolled-back commit inside a synchronous command. Its message is user-facing
    /// (a <see cref="LeaseResult.DenialMessage"/> or <see cref="WorksharingMessages.RolledBack"/>); the command
    /// shows it and returns <c>Result.Failed</c> so Revit discards the run's earlier commits.
    /// </summary>
    public sealed class WorksharingException : Exception
    {
        public WorksharingException(string message) : base(message) { }
    }
}
