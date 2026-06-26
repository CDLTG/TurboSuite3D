#nullable disable
using System;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Dmx.Services;
using TurboSuite.Dmx.ViewModels;
using TurboSuite.Dmx.Views;
using TurboSuite.Shared.Services;

namespace TurboSuite.Dmx;

/// <summary>
/// TurboDMX — DMX-controlled RGBW LED tape automation (decoder/driver packing, addressing, one-line
/// generation). The Revit-coupled front end of the pure engine in <c>Core/Dmx/</c>.
///
/// STATE: experimental — registered only when <c>ExperimentalCommandsEnabled</c> is set
/// (App/TurboSuiteApplication.cs), ribbon still uses the <c>Blank</c> placeholder icon. Phase 1 is
/// READ-ONLY: it reads the model into a snapshot, opens the modeless window, and shows a live bill off the
/// declarations. No model writes (those start in Phase 2).
///
/// MODELESS (TurboNumber/TurboZones pattern): the initial read happens here before the window opens; the
/// Refresh re-read routes through an <see cref="Autodesk.Revit.UI.IExternalEventHandler"/>-backed work
/// queue so it runs on the Revit API thread.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class DmxCommand : IExternalCommand
{
    private static TurboDmxWindow _activeWindow;

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
                TaskDialog.Show("TurboDMX", "No active document found.");
                return Result.Failed;
            }

            if (doc.IsModifiable)
            {
                TaskDialog.Show("TurboDMX", "Please close any active transactions before opening TurboDMX.");
                return Result.Failed;
            }

            var reader = new DmxModelReader(doc);
            var snapshot = reader.Read();

            // Read-only phase, but the Refresh re-read still goes through the Revit thread.
            var workQueue = new RevitWorkQueue("TurboDMX Error", "TurboDMX Work Queue");
            var viewModel = new DmxMainViewModel(snapshot, workQueue, reader);

            var window = new TurboDmxWindow { DataContext = viewModel };
            new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };

            window.Closed += (s, e) =>
            {
                _activeWindow = null;
                workQueue.Dispose();
            };

            _activeWindow = window;
            window.Show();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            TaskDialog.Show("TurboDMX Error", $"An unexpected error occurred:\n{ex.Message}");
            return Result.Failed;
        }
    }
}
