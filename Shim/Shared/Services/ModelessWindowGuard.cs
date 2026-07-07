#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

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
    /// </summary>
    public static class ModelessWindowGuard
    {
        private sealed class Entry
        {
            public Document Doc;
            public Action ForceClose;
        }

        private static readonly List<Entry> _entries = new List<Entry>();
        private static bool _hooked;

        /// <summary>Wire the one app-level DocumentClosing hook. Call once from OnStartup.</summary>
        public static void Hook(ControlledApplication app)
        {
            if (_hooked || app == null) return;
            app.DocumentClosing += OnDocumentClosing;
            _hooked = true;
        }

        /// <summary>Register a modeless window so it auto-closes when <paramref name="doc"/> closes. The window
        /// unregisters itself on Closed. <paramref name="forceClose"/> should close the window in a way that
        /// skips any doc-touching teardown that a normal close would defer (see the DMX registration).</summary>
        public static void Register(Document doc, Window window, Action forceClose)
        {
            if (doc == null || window == null || forceClose == null) return;
            var entry = new Entry { Doc = doc, ForceClose = forceClose };
            _entries.Add(entry);
            window.Closed += (s, e) => _entries.Remove(entry);
        }

        private static void OnDocumentClosing(object sender, DocumentClosingEventArgs e)
        {
            Document closing = e.Document;
            // Snapshot first: ForceClose → window.Close → the Closed handler mutates _entries mid-iteration.
            foreach (var entry in _entries.Where(x => IsSameDocument(x.Doc, closing)).ToList())
            {
                try { entry.ForceClose(); }
                catch { /* a window teardown failure must never abort Revit's document close */ }
            }
        }

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
