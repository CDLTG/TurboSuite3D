#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using TurboSuite.Abstractions;
using TurboSuite.Number.Models;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Number.Services
{
    /// <summary>
    /// Shim-side <see cref="ICircuitNumberOperations"/> — binds the Revit-free contract to
    /// the active document and the existing panel-schedule / writer / collector services.
    /// The panel-schedule view handle is a boxed <see cref="PanelScheduleView"/>. Every
    /// method must be invoked on the Revit API thread (via <see cref="RevitWorkQueue"/>).
    /// </summary>
    public class CircuitNumberOperations : ICircuitNumberOperations
    {
        private readonly Document _doc;
        private readonly UIDocument _uidoc;
        private readonly PanelScheduleService _panelScheduleService;
        private readonly NumberWriterService _writerService;
        private readonly NumberCollectorService _collectorService;

        public CircuitNumberOperations(Document doc, UIDocument uidoc,
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

        public object GetOrCreateScheduleView(ElementRef panelRef)
            => _panelScheduleService.GetOrCreateScheduleView(_doc, panelRef.ToElementId());

        public IReadOnlyList<CircuitSlotData> GetSlotLayout(object scheduleView)
        {
            var psv = (PanelScheduleView)scheduleView;
            var result = new List<CircuitSlotData>();

            foreach (var slot in _panelScheduleService.GetSlotLayout(psv, _doc))
            {
                // Empty/Spare/Space slots: no real circuit element.
                if (slot.CircuitId == null || slot.CircuitId == ElementId.InvalidElementId)
                {
                    result.Add(new CircuitSlotData
                    {
                        CircuitRef = ElementRef.None,
                        CircuitNumber = "",
                        LoadName = SlotLoadName(slot.SlotType, fallback: ""),
                        SlotNumber = slot.SlotNumber,
                        SlotRow = slot.Row,
                        SlotCol = slot.Col,
                        SlotType = slot.SlotType
                    });
                    continue;
                }

                // Real circuit: resolve number + load name. Non-circuit elements are
                // omitted (matches the original VM projection's `is ElectricalSystem` gate).
                if (_doc.GetElement(slot.CircuitId) is ElectricalSystem es)
                {
                    result.Add(new CircuitSlotData
                    {
                        CircuitRef = es.Id.ToRef(),
                        CircuitNumber = ParameterHelper.GetCircuitNumber(es),
                        LoadName = SlotLoadName(slot.SlotType, fallback: ParameterHelper.GetLoadName(es) ?? ""),
                        SlotNumber = slot.SlotNumber,
                        SlotRow = slot.Row,
                        SlotCol = slot.Col,
                        SlotType = slot.SlotType
                    });
                }
            }

            return result;
        }

        private static string SlotLoadName(string slotType, string fallback)
            => slotType == "Spare" ? "(Spare)"
             : slotType == "Space" ? "(Space)"
             : fallback;

        public bool MoveCircuit(object scheduleView, int fromRow, int fromCol, int toRow, int toCol)
            => _panelScheduleService.MoveCircuit(_doc, (PanelScheduleView)scheduleView, fromRow, fromCol, toRow, toCol);

        public bool AssignSpare(object scheduleView, IReadOnlyList<(int Row, int Col)> slots)
            => _panelScheduleService.AssignSpareMultiple(_doc, (PanelScheduleView)scheduleView, slots.ToList());

        public bool AssignSpace(object scheduleView, IReadOnlyList<(int Row, int Col)> slots)
            => _panelScheduleService.AssignSpaceMultiple(_doc, (PanelScheduleView)scheduleView, slots.ToList());

        public bool RemoveSpareSpace(object scheduleView, IReadOnlyList<(int Row, int Col, string SlotType)> slots)
            => _panelScheduleService.RemoveSpareSpaceMultiple(_doc, (PanelScheduleView)scheduleView, slots.ToList());

        public void OpenScheduleView(object scheduleView)
        {
            if (scheduleView is PanelScheduleView psv)
                _uidoc.RequestViewChange(psv);
        }

        public void WritePanelSettings(IReadOnlyList<PanelSettingsModel> panelSettings)
            => _writerService.WritePanelSettings(_doc, panelSettings);

        public IReadOnlyList<CircuitNumberRow> RefreshCircuits()
            => _collectorService.GetCircuits(_doc);
    }
}
