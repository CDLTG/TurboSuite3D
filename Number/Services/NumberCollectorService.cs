#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Number.Models;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Services;

namespace TurboSuite.Number.Services
{
    public class NumberCollectorService
    {
        public List<CircuitNumberRow> GetCircuits(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem))
                .Cast<ElectricalSystem>()
                .OrderBy(es => ParameterHelper.GetPanelName(es) ?? "")
                .ThenBy(es => ParameterHelper.GetCircuitNumber(es))
                .Select(es => new CircuitNumberRow
                {
                    ElementId = es.Id,
                    CircuitNumber = ParameterHelper.GetCircuitNumber(es),
                    Panel = ParameterHelper.GetPanelName(es),
                    LoadName = ParameterHelper.GetLoadName(es)
                })
                .ToList();
        }

        public List<DeviceNumberRow> GetKeypads(Document doc)
        {
            var regionFallback = new RegionRoomLookupService(doc);
            var roomCache = new LinkedRoomFinderService.RoomLookupCache(doc, regionFallback);
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_LightingDevices)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi =>
                {
                    string familyName = fi.Symbol?.Family?.Name ?? "";
                    return familyName.ToLowerInvariant().Contains("keypad");
                })
                .Select(fi =>
                {
                    Room room = roomCache.FindRoom(fi);
                    string roomName = room != null
                        ? room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? ""
                        : roomCache.FindRoomName(fi) ?? "";
                    string roomNumber = room?.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? "";
                    return new DeviceNumberRow
                    {
                        ElementId = fi.Id,
                        FamilyName = fi.Symbol?.Family?.Name ?? "",
                        TypeName = TrimTypePrefix(fi.Symbol?.Name ?? ""),
                        Model = fi.Symbol?.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL)?.AsString() ?? "",
                        SwitchId = ParameterHelper.GetSwitchID(fi) ?? "",
                        RoomName = roomName,
                        RoomNumber = roomNumber,
                        Mark = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? ""
                    };
                })
                .OrderBy(d => d.Mark, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public List<DeviceNumberRow> GetPowerSupplies(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_LightingDevices)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Symbol?.LookupParameter("Sub-Driver Power") != null)
                .Select(fi =>
                {
                    var circuit = fi.MEPModel?.GetElectricalSystems()?.FirstOrDefault();
                    return new DeviceNumberRow
                    {
                        ElementId = fi.Id,
                        FamilyName = fi.Symbol?.Family?.Name ?? "",
                        TypeName = TrimTypePrefix(fi.Symbol?.Name ?? ""),
                        Model = fi.Symbol?.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL)?.AsString() ?? "",
                        SwitchId = ParameterHelper.GetSwitchID(fi) ?? "",
                        CircuitNumber = fi.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER)?.AsString() ?? "",
                        LoadName = circuit != null ? ParameterHelper.GetLoadName(circuit) ?? "" : "",
                        Mark = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? ""
                    };
                })
                .OrderBy(d => d.Mark, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string TrimTypePrefix(string typeName)
        {
            int index = typeName.LastIndexOf(". ");
            return index >= 0 ? typeName.Substring(index + 2) : typeName;
        }
    }
}
