#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Name.Services;
using TurboSuite.Name.ViewModels;
using TurboSuite.Name.Views;
using TurboSuite.Shared.Services;

namespace TurboSuite.Name
{
    /// <summary>
    /// TurboName — opens the one modeless window for CAD-based room-name assignment and region generation.
    /// Every Revit write (pick loops, auto-generate, Assign Room Names, settings save) goes through the single
    /// <see cref="TurboNameApiHandler"/> external event (see CLAUDE.md "Modeless pattern"). Going modeless also
    /// retires TurboName's old Cancelled-return rollback hazard — the settings save now commits inside the
    /// external event, not synchronously in Execute.
    ///
    /// <para><b>Worksharing:</b> the window is bound to one plan view and most of its writes are view-level (CAD
    /// layer visibility / line graphics / previews), so the session lease (<see cref="WorksharingLease.AcquireSession"/>)
    /// takes the TurboName lock store, the CAD Room Source store, the view, and the linked CAD imports in it (their
    /// pin is flipped to force a graphics regen). Region clears and Assign Room Names lease their regions per
    /// operation. The borrows hold until the user syncs, and a successful sync closes the window
    /// (<see cref="ModelessWindowGuard"/>).</para>
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class NameCommand : IExternalCommand
    {
        private const string TextNoteTypeName = "AL_Annotation_4.5\"";
        private const string DescriptionTextNoteTypeName = "AL_Annotation_3\"";

        private static TurboNameWindow _activeWindow;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (_activeWindow != null)
                {
                    _activeWindow.Activate();
                    return Result.Succeeded;
                }

                UIDocument uidoc = commandData.Application.ActiveUIDocument;
                Document doc = uidoc?.Document;

                if (doc == null)
                {
                    TaskDialog.Show("TurboName", "No active document found.");
                    return Result.Failed;
                }

                if (doc.IsModifiable)
                {
                    TaskDialog.Show("TurboName", "Please close any active transactions before opening TurboName.");
                    return Result.Failed;
                }

                View view = doc.ActiveView;

                // Returns Succeeded on denial, not Cancelled: a first-ever open may have just committed the
                // lock store, and a non-Succeeded return would roll it back. The working set is built only on a
                // workshared doc — the view-scoped import collector throws on views that can't be filtered,
                // which a non-workshared open must never trip over.
                if (doc.IsWorkshared)
                {
                    var sessionSet = new List<ElementId> { view.Id };
                    sessionSet.AddRange(CadLinkResolver.GetLinkedImports(doc, view).Select(i => i.Id));
                    var settingsStore = CadRoomSourceStorageService.FindStorageId(doc);
                    if (settingsStore != null) sessionSet.Add(settingsStore);
                    var lease = WorksharingLease.AcquireSession(doc, "TurboName", sessionSet);
                    if (!lease.IsGranted)
                    {
                        TaskDialog.Show("TurboName", lease.DenialMessage);
                        return Result.Succeeded;
                    }
                }

                var cadSettings = CadRoomSourceSettingsCache.Get(doc);

                // Construction order: the handler reads the live settings via a provider that captures the VM
                // (assigned just below), the event wraps the handler, and the VM drives both.
                TurboNameViewModel vm = null;
                var handler = new TurboNameApiHandler(
                    doc, uidoc, view,
                    () => vm.CadConfig.ToModel(),
                    TextNoteTypeName, DescriptionTextNoteTypeName);
                var externalEvent = ExternalEvent.Create(handler);
                vm = new TurboNameViewModel(cadSettings, uidoc, view, externalEvent, handler);

                var window = new TurboNameWindow { DataContext = vm };
                new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };

                window.Closed += (s, e) =>
                {
                    _activeWindow = null;
                    externalEvent.Dispose();
                };

                // forceClose skips the on-close save round-trip — during DocumentClosing we must not raise the
                // shared external event against a closing document.
                ModelessWindowGuard.Register(doc, window, window.ForceClose);
                _activeWindow = window;
                window.Show();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("TurboName Error", $"An unexpected error occurred:\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}
