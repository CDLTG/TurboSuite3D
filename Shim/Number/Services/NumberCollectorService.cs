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
                .Where(es => !IsExcludedCircuit(es))
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

        /// <summary>
        /// Circuits kept out of the Circuit Numbers summary + its duplicate-number flag. Each is
        /// deliberately unpaneled or a panel artifact, so listing them only adds noise (and, since
        /// they share a circuit-number string, false red duplicate flags):
        /// <list type="bullet">
        /// <item>Panel <b>Feed Through Lugs</b> — a panel artifact, never a designed circuit (same
        /// drop the Load Schedule makes in <c>LoadsCollectorService</c>).</item>
        /// <item><b>Switched</b> circuits — TurboWire local switch legs, &lt;unnamed&gt; by design.</item>
        /// <item><b>DMX / DALI</b> zone circuits — owned by the control subsystem, unpaneled by design;
        /// detected by any member fixture's <c>Dimming Protocol</c>.</item>
        /// </list>
        /// A genuinely overlooked (forgotten-unpaneled) circuit carries none of these signals, so it
        /// still shows as &lt;unnamed&gt; — the point of keeping that surface.
        /// </summary>
        private static bool IsExcludedCircuit(ElectricalSystem circuit)
        {
            string circuitNumber = ParameterHelper.GetCircuitNumber(circuit);
            if (!string.IsNullOrEmpty(circuitNumber) &&
                circuitNumber.Contains("Feed Through Lugs", StringComparison.OrdinalIgnoreCase))
                return true;

            if (CircuitService.IsSwitchedCircuit(circuit))
                return true;

            if (IsControlSubsystemCircuit(circuit))
                return true;

            return false;
        }

        /// <summary>Whether any member fixture runs DMX or DALI — the signal that this &lt;unnamed&gt;
        /// circuit is a control-subsystem zone (created unpaneled by TurboDMX/TurboDALI), not an oversight.</summary>
        private static bool IsControlSubsystemCircuit(ElectricalSystem circuit)
        {
            if (circuit.Elements == null) return false;

            foreach (Element el in circuit.Elements)
            {
                if (el is not FamilyInstance fi) continue;
                string protocol = ParameterHelper.GetDimmingProtocol(fi);
                if (string.Equals(protocol, "DMX", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(protocol, "DALI", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
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
