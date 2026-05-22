#nullable disable
using System;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TurboSuite.Zones.Services
{
    public class RevitApiRequestHandler : IExternalEventHandler
    {
        private readonly Document _doc;
        private readonly UIDocument _uidoc;
        private readonly LoadNameService _loadNameService;

        public RevitApiRequest CurrentRequest { get; set; }

        public RevitApiRequestHandler(Document doc, UIDocument uidoc, LoadNameService loadNameService)
        {
            _doc = doc;
            _uidoc = uidoc;
            _loadNameService = loadNameService;
        }

        public void Execute(UIApplication app)
        {
            var request = CurrentRequest;
            if (request == null) return;

            try
            {
                switch (request)
                {
                    case UpdateLoadNamesRequest r:
                        int count = _loadNameService.UpdateLoadNames(_doc, r.Circuits);
                        Dispatch(r.OnComplete, count);
                        break;

                    case SavePanelSettingsRequest r:
                        ZonesPanelSettingsStorageService.Save(_doc, r.Settings);
                        Dispatch(r.OnComplete, true);
                        break;

                    case SelectInProjectRequest r:
                        var elem = _doc.GetElement(r.CircuitId);
                        if (elem == null)
                        {
                            Dispatch(r.OnComplete, false);
                            break;
                        }
                        _uidoc.Selection.SetElementIds(new[] { r.CircuitId });
                        _uidoc.ShowElements(r.CircuitId);
                        Dispatch(r.OnComplete, true);
                        break;
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("TurboZones Error", $"An error occurred:\n{ex.Message}");
                Dispatch(request.OnComplete, null);
            }
            finally
            {
                CurrentRequest = null;
            }
        }

        public string GetName() => "TurboZones API Handler";

        private static void Dispatch(Action<object> callback, object result)
        {
            if (callback == null) return;
            Application.Current?.Dispatcher?.Invoke(() => callback(result));
        }
    }
}
