#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using TurboSuite.Abstractions;
using TurboSuite.Number.Models;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Services;

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

        public bool SortPanelByRoomOrder(object scheduleView, IReadOnlyList<string> roomOrder)
        {
            var psv = (PanelScheduleView)scheduleView;

            // Resolve each circuit's room the same way TurboZones does — persisted override
            // wins, else the first fixture's Space/region — so the two agree on room names.
            var roomCache = new SpaceRoomFinderService.SpaceLookupCache(_doc, new RegionRoomLookupService(_doc));
            var overrides = RoomOverrideStorageService.Load(_doc);

            // Full layout (all slot kinds — the Core GetSlotLayout omits non-circuits, which
            // this needs to compact/clear). SlotItem[] runs parallel to slotInfos by index.
            var slotInfos = _panelScheduleService.GetSlotLayout(psv, _doc);
            var items = new List<RoomOrderPanelSorter.SlotItem>(slotInfos.Count);
            var display = new string[slotInfos.Count];

            for (int i = 0; i < slotInfos.Count; i++)
            {
                var slot = slotInfos[i];
                bool isCircuit = slot.SlotType == "Circuit";
                string room = "";
                if (isCircuit && _doc.GetElement(slot.CircuitId) is ElectricalSystem es)
                {
                    if (overrides.TryGetValue(es.UniqueId, out var o) && !string.IsNullOrWhiteSpace(o))
                        room = o;
                    else
                    {
                        var fixtures = CircuitService.GetFixturesOnCircuit(es);
                        room = fixtures.Count > 0 ? (roomCache.FindRoomName(fixtures[0]) ?? "") : "";
                    }
                    string number = ParameterHelper.GetCircuitNumber(es);
                    string load = ParameterHelper.GetLoadName(es) ?? "";
                    string roomLabel = string.IsNullOrWhiteSpace(room) ? "(no room)" : room;
                    display[i] = $"{number}  ·  {roomLabel}" + (string.IsNullOrWhiteSpace(load) ? "" : $"  ·  {load}");
                }
                else
                {
                    display[i] = $"({slot.SlotType})";
                }
                items.Add(new RoomOrderPanelSorter.SlotItem(isCircuit, room));
            }

            var swaps = RoomOrderPanelSorter.ComputeSwaps(items, roomOrder);

            var spareSpaceCells = slotInfos
                .Where(s => s.SlotType == "Spare" || s.SlotType == "Space")
                .Select(s => (s.Row, s.Col))
                .ToList();

            if (swaps.Count == 0 && spareSpaceCells.Count == 0)
            {
                TaskDialog.Show("TurboNumber", "Panel is already in room order.");
                return false;
            }

            // Post-sort preview: apply the swaps to the display labels (mirrors the shim's
            // MoveSlotTo loop) and list the resulting circuit order.
            var sortedDisplay = (string[])display.Clone();
            foreach (var (a, b) in swaps)
                (sortedDisplay[a], sortedDisplay[b]) = (sortedDisplay[b], sortedDisplay[a]);

            int circuitCount = items.Count(x => x.IsCircuit);
            int roomCount = items.Where(x => x.IsCircuit && !string.IsNullOrWhiteSpace(x.RoomName))
                .Select(x => x.RoomName)
                .Distinct(System.StringComparer.OrdinalIgnoreCase)
                .Count();
            int spareCount = slotInfos.Count(s => s.SlotType == "Spare");
            int spaceCount = slotInfos.Count(s => s.SlotType == "Space");

            var targetLines = new List<string>();
            for (int i = 0; i < sortedDisplay.Length; i++)
            {
                if (!sortedDisplay[i].StartsWith("(")) // skip the sunk non-circuit slots
                    targetLines.Add($"{i + 1}.  {sortedDisplay[i]}");
            }

            var dlg = new TaskDialog("TurboNumber")
            {
                MainInstruction = $"Sort panel \"{psv.Name}\" by room order?",
                MainContent = $"{circuitCount} circuits across {roomCount} rooms, {swaps.Count} moves." +
                    (spareCount + spaceCount > 0
                        ? $"\n{spareCount} spares and {spaceCount} spaces will be cleared to Empty."
                        : ""),
                ExpandedContent = string.Join("\n", targetLines),
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            if (dlg.Show() != TaskDialogResult.Yes)
                return false;

            return _panelScheduleService.ApplyRoomSort(_doc, psv, spareSpaceCells, swaps, slotInfos);
        }

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
