#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using TurboSuite.Number.Models;
using TurboSuite.Shared.Constants;
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
                    ElementId = es.Id.ToRef(),
                    CircuitNumber = ParameterHelper.GetCircuitNumber(es),
                    Panel = ParameterHelper.GetPanelName(es),
                    LoadName = ParameterHelper.GetLoadName(es)
                })
                .ToList();
        }

        public List<DeviceNumberRow> GetKeypads(Document doc)
        {
            var regionFallback = new RegionRoomLookupService(doc);
            var roomCache = new SpaceRoomFinderService.SpaceLookupCache(doc, regionFallback);
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_LightingDevices)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => ParameterHelper.GetRole(fi) == Roles.Keypad)
                .Select(fi =>
                {
                    Space space = roomCache.FindSpace(fi);
                    string roomName = space != null
                        ? SpaceRoomFinderService.ReadSpaceName(space)
                        : roomCache.FindRoomName(fi) ?? "";
                    string roomNumber = space != null ? SpaceRoomFinderService.ReadSpaceNumber(space) : "";
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
                .Where(fi => fi.Symbol?.LookupParameter(ParameterNames.SubDriverPower) != null)
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
                        CircuitElementId = circuit?.Id ?? ElementId.InvalidElementId,
                        LoadName = circuit != null ? ParameterHelper.GetLoadName(circuit) ?? "" : "",
                        Mark = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "",
                        PositionY = GeometryHelper.GetFixtureLocation(fi)?.Y ?? 0.0
                    };
                })
                .OrderBy(d => d.Mark, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Every distinct room name in the project — the union of all MEP Space names (3D)
        /// and Room Region names (2D) — for seeding the project-wide room-order list, so
        /// keypad-less and circuit-less rooms are orderable too. Space names come from
        /// <see cref="SpaceRoomFinderService.ReadSpaceName"/> and circuit rooms resolve via
        /// <c>FindRoomName</c> (which also returns <c>ReadSpaceName</c>), so the order-list
        /// keys and the sort keys are identical strings — no drift.
        /// </summary>
        public List<string> GetAllRoomNames(Document doc)
        {
            var spaceNames = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MEPSpaces)
                .WhereElementIsNotElementType()
                .Cast<Space>()
                .Where(s => s.Area > 0)
                .Select(SpaceRoomFinderService.ReadSpaceName);

            var regionNames = new RegionRoomLookupService(doc).RoomNames;

            return spaceNames
                .Concat(regionNames)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, NaturalStringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string TrimTypePrefix(string typeName)
        {
            int index = typeName.LastIndexOf(". ");
            return index >= 0 ? typeName.Substring(index + 2) : typeName;
        }
    }
}
