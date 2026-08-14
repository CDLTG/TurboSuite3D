#nullable disable
using System;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Dali.Services;
using TurboSuite.Dali.ViewModels;
using TurboSuite.Dali.Views;
using TurboSuite.Shared.Services;

namespace TurboSuite.Dali
{
    /// <summary>
    /// TurboDALI — the standalone DALI addressing command. Owns DALI end to end: collect DALI fixtures, group
    /// Control Zones into loops, declare each loop's panel ZONE, and assign + write back per-circuit addresses
    /// (with a job-wide numbering lock). TurboZones stays a pure consumer of the resulting demand + placement.
    ///
    /// STATE: experimental — registered only when <c>ExperimentalCommandsEnabled</c> is set
    /// (App/TurboSuiteApplication.cs). It is the <b>sole writer</b> of the DALI schema: the transitional
    /// TurboZones DALI tab has been removed, so DALI loop declaration is dev-only until this command ungates.
    ///
    /// INDEPENDENT COLLECTION: TurboDALI reads its own inputs from the doc — DALI fixtures/zones,
    /// and the model-derived panel-ZONE list via <c>PanelAllocationService.DiscoverPanelZones</c> — so it has
    /// no read/write dependency on TurboZones' persisted state.
    ///
    /// MODELESS (TurboZones/TurboDMX pattern): the read + state load happen here before the window opens; the
    /// coalesced state save routes through the shared <see cref="RevitWorkQueue"/> so its transaction runs on
    /// the Revit API thread.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class DaliCommand : IExternalCommand
    {
        private static TurboDaliWindow _activeWindow;

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
                    TaskDialog.Show("TurboDALI", "No active document found.");
                    return Result.Failed;
                }

                if (doc.IsModifiable)
                {
                    TaskDialog.Show("TurboDALI", "Please close any active transactions before opening TurboDALI.");
                    return Result.Failed;
                }

                // ── Independent collection ─────────────────────────────────────────────────────────────────
                // One collector reads the DALI zones + panel-ZONE list + persisted loops from the model, at
                // open AND on Refresh — TurboDALI discovers it all itself, no dependency on TurboZones' state.
                var inputProvider = new DaliTabInputProvider(doc);
                var inputs = inputProvider.Read();

                var workQueue = new RevitWorkQueue("TurboDALI Error", "TurboDALI Work Queue");
                var store = new DaliLoopStore(doc);   // TurboDALI is the sole writer of the DALI schema

                var tab = new DaliTabViewModel(inputs.Zones, inputs.PanelZones, inputs.Saved, workQueue, store);

                // Addressing seams: read the model, write the "DALI Address" param, color the
                // active-view zones — all routed through the work queue by the ViewModel.
                var reader = new DaliModelReader(doc);
                var writer = new DaliAddressWriter(doc);
                var zoneColor = new DaliZoneColorService(uidoc);

                // Yes/No gate for the destructive numbering-lock actions (Re-lock / Unlock).
                Func<string, bool> confirm = msg =>
                    new TaskDialog("TurboDALI")
                    {
                        MainInstruction = "Numbering lock",
                        MainContent = msg,
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                        DefaultButton = TaskDialogResult.No,
                    }.Show() == TaskDialogResult.Yes;

                var viewModel = new DaliMainViewModel(tab, workQueue, reader, writer, zoneColor, store,
                                                      inputProvider, inputs.Saved, confirm);

                var window = new TurboDaliWindow { DataContext = viewModel };
                new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };

                // Deferred close: revert the active-view zone overlay on the API thread first (mirrors
                // TurboDMX). The first Closing cancels and queues the revert; its completion re-issues Close.
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

                // If the project closes out from under us, force-close SKIPPING the deferred overlay revert —
                // the closing document's view overrides are discarded with it, so the queued revert would run
                // after the doc is gone (crash). Setting `reverted` lets Closing pass straight through.
                ModelessWindowGuard.Register(doc, window, () => { reverted = true; window.Close(); });

                _activeWindow = window;
                window.Show();

                // Color the active view's zones on open (after Show so the window owns focus first).
                viewModel.ApplyZoneColors();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("TurboDALI Error", $"An unexpected error occurred:\n{ex.Message}");
                return Result.Failed;
            }
        }
    }
}
