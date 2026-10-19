#nullable disable
using System;
using System.Collections.Generic;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Schedule.Models;
using TurboSuite.Schedule.Services;
using TurboSuite.Schedule.ViewModels;
using TurboSuite.Schedule.Views;
using TurboSuite.Shared.Services;

namespace TurboSuite.Schedule
{
    /// <summary>
    /// TurboSchedule — modeless page-per-Type-Mark spec editor for lighting fixtures and drivers.
    /// All Revit writes go through <see cref="RevitWorkQueue"/>; see CLAUDE.md "Modeless pattern".
    ///
    /// <para><b>Worksharing:</b> the window holds a session lease (<see cref="WorksharingLease.AcquireSession"/>)
    /// on the TurboSchedule lock store plus every lighting fixture/driver type it could write, taken before the
    /// window opens. If any of it is held by a teammate or stale, the window doesn't open — so a save can never
    /// be silently rolled back for ownership. The borrows hold until the user syncs, and a successful sync
    /// closes the window (<see cref="ModelessWindowGuard"/>).</para>
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ScheduleCommand : IExternalCommand
    {
        private static TurboScheduleWindow _activeWindow;

        // Last-selected type, remembered across close/reopen for the life of the Revit session.
        private static string _lastTypeMark;
        private static PageKind? _lastKind;

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
                    TaskDialog.Show("TurboSchedule", "No active document found.");
                    return Result.Failed;
                }

                if (doc.IsModifiable)
                {
                    TaskDialog.Show("TurboSchedule", "Please close any active transactions before opening TurboSchedule.");
                    return Result.Failed;
                }

                var pages = ScheduleTypeCollector.Collect(doc);
                if (pages.Count == 0)
                {
                    TaskDialog.Show("TurboSchedule",
                        "No lighting fixture or driver types with a Type Mark were found in this project.");
                    return Result.Cancelled;
                }

                // Returns Succeeded on denial, not Cancelled: a first-ever open may have just committed the
                // lock store, and a non-Succeeded return would roll it back.
                var lease = WorksharingLease.AcquireSession(doc, "TurboSchedule", LeaseWorkingSet(doc), "lighting type");
                if (!lease.IsGranted)
                {
                    TaskDialog.Show("TurboSchedule", lease.DenialMessage);
                    return Result.Succeeded;
                }

                var workQueue = new RevitWorkQueue("TurboSchedule Error", "TurboSchedule Work Queue");
                var writer = new ScheduleWriterService(doc);
                var gateway = new ScheduleWorkbookGateway(doc, workQueue, writer);
                var viewModel = new ScheduleMainViewModel(pages, workQueue, writer, gateway,
                    _lastTypeMark, _lastKind);

                var window = new TurboScheduleWindow { DataContext = viewModel };
                new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };

                window.Closed += (s, e) =>
                {
                    _lastTypeMark = viewModel.CurrentPage?.TypeMark;
                    _lastKind = viewModel.CurrentPage?.Kind;
                    _activeWindow = null;
                    workQueue.Dispose();
                };

                ModelessWindowGuard.Register(doc, window, window.ForceClose,
                    () => viewModel.HasUnsavedChanges, "TurboSchedule");
                _activeWindow = window;
                window.Show();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("TurboSchedule Error", $"An unexpected error occurred:\n{ex.Message}");
                return Result.Failed;
            }
        }

        // Everything the window can write: every lighting fixture/driver type (placed or not — the writer
        // re-resolves by Type Mark at save time, so a type placed mid-session is already covered) plus the
        // workbook-path store if it exists.
        private static List<ElementId> LeaseWorkingSet(Document doc)
        {
            var ids = new List<ElementId>();
            foreach (var cat in new[] { BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingDevices })
            {
                using var collector = new FilteredElementCollector(doc);
                ids.AddRange(collector.OfCategory(cat).OfClass(typeof(FamilySymbol)).ToElementIds());
            }
            var pathStore = WorkbookPathStorageService.FindStorageId(doc);
            if (pathStore != null) ids.Add(pathStore);
            return ids;
        }
    }
}
