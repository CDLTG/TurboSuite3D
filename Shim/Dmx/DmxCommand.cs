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
/// (App/TurboSuiteApplication.cs), ribbon still uses the <c>Blank</c> placeholder icon. The model READ is
/// still read-only (no element creation/modification — that's placement, pending); adds the
/// first writes as doc-side ExtensibleStorage persistence of the declarations (settings, curated part pools,
/// declared loops) so the window reopens where the designer left it.
///
/// MODELESS (TurboNumber/TurboZones pattern): the initial read + state load happen here before the window
/// opens; the Refresh re-read and the coalesced state save both route through an
/// <see cref="Autodesk.Revit.UI.IExternalEventHandler"/>-backed work queue so they run on the Revit API thread.
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
            var state = DmxStorageService.Load(doc);

            // The Refresh re-read AND the doc-side state save ( loop persistence) both go through the
            // Revit API thread via the shared work queue; the persister coalesces save bursts into one tx.
            var workQueue = new RevitWorkQueue("TurboDMX Error", "TurboDMX Work Queue");
            var persister = new DmxStatePersister(workQueue, doc);
            var placement = new DmxPlacementService(uidoc);
            var oneLine = new DmxOneLineService(uidoc);
            var selection = new DmxModelSelection(uidoc);
            var zoneColor = new DmxZoneColorService(uidoc); // active-view zone overlay

            // Yes/No gate for the destructive numbering-lock actions (Re-lock / Unlock).
            System.Func<string, bool> confirm = msg =>
                new TaskDialog("TurboDMX")
                {
                    MainInstruction = "Numbering lock",
                    MainContent = msg,
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No,
                }.Show() == TaskDialogResult.Yes;

            var viewModel = new DmxMainViewModel(snapshot, state, workQueue, reader,
                                                 persister.Save, placement, confirm, selection, oneLine, zoneColor);

            var window = new TurboDmxWindow { DataContext = viewModel };
            new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };

            // : defer the close until the active-view zone overlay reverts on the API thread. The first
            // Closing cancels and queues the revert; its completion (UI thread) re-issues Close, which passes.
            bool reverted = false;
            window.Closing += (s, e) =>
            {
                if (reverted) return;
                e.Cancel = true;
                viewModel.RevertZoneColors(() => { reverted = true; window.Close(); });
            };
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
