#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace TurboSuite.Shared.Services
{
    /// <summary>
    /// Shared safety net for the suite's modeless windows. A modeless window is opened against one document
    /// and holds live references to it (UIDocument, external-event handlers, view-override state). If the user
    /// closes that project while the window is still open, those references go dead — and the next interaction,
    /// or just closing the window, hard-crashes Revit (notably TurboDMX, whose close reverts active-view
    /// overrides on the API thread).
    ///
    /// Each modeless command registers its window + the document it was opened against + a force-close action.
    /// A single app-level <see cref="ControlledApplication.DocumentClosing"/> hook (wired once from OnStartup)
    /// fires the force-close for every window tied to the closing document. Because DocumentClosing fires while
    /// the document is still open and valid, each window's own teardown (queue dispose, override revert) runs
    /// against a live document and is safe. Windows unregister themselves on Closed.
    ///
    /// <para><b>Worksharing.</b> A successful Sync with Central or Reload Latest also closes that document's
    /// windows: sync relinquishes the session's borrows (see <see cref="WorksharingLease"/>), and both bring in
    /// newer model data the window's in-memory snapshot doesn't have. Reopening re-acquires a fresh lease. These
    /// close <i>gracefully</i> (<see cref="Window.Close"/>, so a window's normal deferred teardown runs against the
    /// still-open document), except a window whose unsaved edits the user already chose to drop. A cancelled or
    /// failed sync/reload changes nothing — borrows are still held and local is unchanged — so windows stay.</para>
    ///
    /// <para>A window with explicit-save dirty state registers <c>hasUnsavedEdits</c>; before a sync or a project
    /// close the user is asked whether to cancel it (both events are cancellable), so unsaved input is never
    /// dropped without a prompt.</para>
    /// </summary>
    public static class ModelessWindowGuard
    {
        private sealed class Entry
        {
            public Document Doc;
            public Window Window;
            public Action ForceClose;
            public Func<bool> HasUnsavedEdits;
            public string Name;
            // Set when the user let a sync proceed over this window's unsaved edits: the post-sync close then
            // force-closes (discarding, no second prompt) instead of closing gracefully.
            public bool DiscardOnSync;

            public bool IsDirty
            {
                get
                {
                    try { return HasUnsavedEdits != null && HasUnsavedEdits(); }
                    catch { return false; }
                }
            }
        }

        private static readonly List<Entry> _entries = new List<Entry>();
        private static bool _hooked;

        /// <summary>Wire the app-level DocumentClosing and sync/reload hooks. Call once from OnStartup.</summary>
        public static void Hook(ControlledApplication app)
        {
            if (_hooked || app == null) return;
            app.DocumentClosing += OnDocumentClosing;
            app.DocumentSynchronizingWithCentral += OnDocumentSynchronizing;
            app.DocumentSynchronizedWithCentral += OnDocumentSynchronized;
            app.DocumentReloadedLatest += OnDocumentReloadedLatest;
            _hooked = true;
        }

        /// <summary>Register a modeless window so it auto-closes when <paramref name="doc"/> closes. The window
        /// unregisters itself on Closed. <paramref name="forceClose"/> should close the window in a way that
        /// skips any doc-touching teardown that a normal close would defer (see the DMX registration), and must not
        /// prompt. A window with explicit-save state passes <paramref name="hasUnsavedEdits"/> and its display
        /// <paramref name="name"/> so a sync or project close asks before its edits are dropped.</summary>
        public static void Register(Document doc, Window window, Action forceClose,
            Func<bool> hasUnsavedEdits = null, string name = null)
        {
            if (doc == null || window == null || forceClose == null) return;
            var entry = new Entry
            {
                Doc = doc, Window = window, ForceClose = forceClose,
                HasUnsavedEdits = hasUnsavedEdits, Name = name ?? window.Title,
            };
            _entries.Add(entry);
            window.Closed += (s, e) => _entries.Remove(entry);
        }

        private static void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
        {
            Document closing = e.Document;
            if (e.Cancellable && AskToCancel(EntriesFor(closing).Where(x => x.IsDirty).ToList(),
                    "Cancel closing the project?", "Keep the project open", "Close it and discard the edits"))
            {
                e.Cancel();
                return;
            }

            // Snapshot first: ForceClose → window.Close → the Closed handler mutates _entries mid-iteration.
            foreach (var entry in EntriesFor(closing).ToList())
            {
                try { entry.ForceClose(); }
                catch { /* a window teardown failure must never abort Revit's document close */ }
            }
        }

        // Pre-sync: the sync will relinquish the session's borrows and close the windows, so unsaved input would
        // be lost. Cancelling stops the sync before anything reaches central (spike S8: the "Save to local" step).
        private static void OnDocumentSynchronizing(object sender, DocumentSynchronizingWithCentralEventArgs e)
        {
            var dirty = EntriesFor(e.Document).Where(x => x.IsDirty).ToList();
            foreach (var entry in dirty) entry.DiscardOnSync = false;
            if (dirty.Count == 0 || !e.Cancellable) return;

            if (AskToCancel(dirty, "Cancel the sync? Revit will report it as failed — nothing is lost.",
                    "Cancel the sync", "Sync anyway and discard the edits"))
            {
                e.Cancel();
                return;
            }
            foreach (var entry in dirty) entry.DiscardOnSync = true;
        }

        private static void OnDocumentSynchronized(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            var entries = EntriesFor(e.Document).ToList();
            // The post-event also fires for a cancelled/failed sync: borrows still held, local unchanged — stay open.
            if (e.Status != RevitAPIEventStatus.Succeeded)
            {
                foreach (var entry in entries) entry.DiscardOnSync = false;
                return;
            }
            CloseForStaleModel(entries);
        }

        private static void OnDocumentReloadedLatest(object sender, DocumentReloadedLatestEventArgs e)
        {
            if (e.Status != RevitAPIEventStatus.Succeeded) return;
            CloseForStaleModel(EntriesFor(e.Document).ToList());
        }

        // The document stays open, so close gracefully (the window's own deferred teardown runs as usual) — except
        // where the user already chose to discard at the pre-sync prompt.
        private static void CloseForStaleModel(List<Entry> entries)
        {
            foreach (var entry in entries)
            {
                try
                {
                    if (entry.DiscardOnSync) entry.ForceClose();
                    else entry.Window.Close();
                }
                catch { /* a window teardown failure must never surface from Revit's sync/reload */ }
            }
        }

        // One Yes/No over every dirty window: Yes = cancel the pending Revit action.
        private static bool AskToCancel(List<Entry> dirty, string question, string cancelText, string proceedText)
        {
            if (dirty.Count == 0) return false;
            var names = string.Join(", ", dirty.Select(x => x.Name).Distinct());
            var td = new TaskDialog("TurboSuite")
            {
                MainInstruction = $"{names} {(dirty.Count == 1 ? "has" : "have")} unsaved edits.",
                MainContent = question,
                AllowCancellation = false,
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, cancelText);
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, proceedText);
            try { return td.Show() == TaskDialogResult.CommandLink1; }
            catch { return false; }
        }

        private static IEnumerable<Entry> EntriesFor(Document doc) =>
            _entries.Where(x => IsSameDocument(x.Doc, doc));

        // Revit hands back a DIFFERENT managed Document wrapper for the same open document in a
        // DocumentClosing event than the one captured at command time (confirmed: different runtime hashes for
        // the same .rvt), so reference equality alone never matches. Fall back to path identity — a saved
        // project (every real TurboDMX/TurboZones/... target) has a stable non-empty PathName.
        private static bool IsSameDocument(Document a, Document b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            try
            {
                string pa = a.PathName, pb = b.PathName;
                if (!string.IsNullOrEmpty(pa) && !string.IsNullOrEmpty(pb))
                    return string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase);
            }
            catch { /* PathName can throw on a half-closed doc — fall through */ }
            return false;
        }
    }
}
