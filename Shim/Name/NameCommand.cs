#nullable disable
using System;
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
                var cadSettings = CadRoomSourceSettingsCache.Get(doc);

                // Construction order: the handler reads the live settings via a provider that captures the VM
                // (assigned just below), the event wraps the handler, and the VM drives both.
                TurboNameViewModel vm = null;
                var handler = new TurboNameApiHandler(
                    doc, uidoc, view,
                    () => vm.CadConfig.ToModel(),
                    TextNoteTypeName, DescriptionTextNoteTypeName);
                var externalEvent = ExternalEvent.Create(handler);
                vm = new TurboNameViewModel(cadSettings, uidoc, externalEvent, handler);

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
