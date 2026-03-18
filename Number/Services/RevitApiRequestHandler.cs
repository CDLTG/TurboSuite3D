#nullable disable
using System;
using System.Collections.Generic;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TurboSuite.Number.Services
{
    public class RevitApiRequestHandler : IExternalEventHandler
    {
        private readonly Document _doc;
        private readonly UIDocument _uidoc;
        private readonly PanelScheduleService _panelScheduleService;
        private readonly NumberWriterService _writerService;
        private readonly NumberCollectorService _collectorService;

        public RevitApiRequest CurrentRequest { get; set; }

        public RevitApiRequestHandler(Document doc, UIDocument uidoc,
            PanelScheduleService panelScheduleService,
            NumberWriterService writerService,
            NumberCollectorService collectorService)
        {
            _doc = doc;
            _uidoc = uidoc;
            _panelScheduleService = panelScheduleService;
            _writerService = writerService;
            _collectorService = collectorService;
        }

        public void Execute(UIApplication app)
        {
            var request = CurrentRequest;
            if (request == null) return;

            try
            {
                switch (request)
                {
                    case WritePanelSettingsRequest r:
                        _writerService.WritePanelSettings(_doc, r.PanelSettings);
                        Dispatch(r.OnComplete, true);
                        break;

                    case WriteDeviceSwitchIdsRequest r:
                        _writerService.WriteDeviceSwitchIds(_doc, r.Rows);
                        Dispatch(r.OnComplete, true);
                        break;

                    case GetOrCreateScheduleViewRequest r:
                        var psv = _panelScheduleService.GetOrCreateScheduleView(_doc, r.PanelId);
                        Dispatch(r.OnComplete, psv);
                        break;

                    case GetSlotLayoutRequest r:
                        var slots = _panelScheduleService.GetSlotLayout(r.ScheduleView, _doc);
                        Dispatch(r.OnComplete, slots);
                        break;

                    case MoveCircuitRequest r:
                        var moved = _panelScheduleService.MoveCircuit(_doc, r.ScheduleView,
                            r.FromRow, r.FromCol, r.ToRow, r.ToCol);
                        Dispatch(r.OnComplete, moved);
                        break;

                    case AssignSpareRequest r:
                        var spareResult = _panelScheduleService.AssignSpareMultiple(_doc, r.ScheduleView, r.Slots);
                        Dispatch(r.OnComplete, spareResult);
                        break;

                    case AssignSpaceRequest r:
                        var spaceResult = _panelScheduleService.AssignSpaceMultiple(_doc, r.ScheduleView, r.Slots);
                        Dispatch(r.OnComplete, spaceResult);
                        break;

                    case RemoveSpareSpaceRequest r:
                        var removeResult = _panelScheduleService.RemoveSpareSpaceMultiple(_doc, r.ScheduleView, r.Slots);
                        Dispatch(r.OnComplete, removeResult);
                        break;

                    case SaveRoomOrderRequest r:
                        RoomOrderStorageService.Save(_doc, r.RoomOrder);
                        Dispatch(r.OnComplete, true);
                        break;

                    case SaveSidebarVisibleRequest r:
                        RoomOrderStorageService.SaveSidebarVisible(_doc, r.IsVisible);
                        Dispatch(r.OnComplete, true);
                        break;

                    case SavePrefixSuffixRequest r:
                        RoomOrderStorageService.SavePrefixSuffix(_doc, r.Prefix, r.Suffix);
                        Dispatch(r.OnComplete, true);
                        break;

                    case OpenScheduleViewRequest r:
                        if (r.ScheduleView != null)
                            _uidoc.RequestViewChange(r.ScheduleView);
                        Dispatch(r.OnComplete, true);
                        break;

                    case RefreshCircuitsRequest r:
                        var circuits = _collectorService.GetCircuits(_doc);
                        Dispatch(r.OnComplete, circuits);
                        break;
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("TurboNumber Error", $"An error occurred:\n{ex.Message}");
                Dispatch(request.OnComplete, null);
            }
        }

        public string GetName() => "TurboNumber API Handler";

        private static void Dispatch(Action<object> callback, object result)
        {
            if (callback == null) return;
            Application.Current?.Dispatcher?.Invoke(() => callback(result));
        }
    }
}
