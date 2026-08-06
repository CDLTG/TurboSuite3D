#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Dmx;
using TurboSuite.Driver.Models;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Service for collecting and filtering electrical circuits
    /// </summary>
    public class CircuitCollectorService
    {
        /// <summary>
        /// Get all electrical circuits that have at least one Lighting Fixture with Remote Power Supply checked.
        /// </summary>
        /// <remarks>
        /// Membership is read from each circuit's <see cref="ElectricalSystem.Elements"/> — the
        /// authoritative electrical-system membership — NOT by bucketing elements on their
        /// <c>RBS_ELEC_CIRCUIT_NUMBER</c> string. Circuits not assigned to a panel all report the
        /// same circuit-number string (e.g. "&lt;unnamed&gt;"), so the old string-keyed approach
        /// merged every unassigned circuit's fixtures onto every unassigned row (yielding an
        /// identical, hugely-oversized recommendation on dozens of rows) and re-ran the
        /// recommendation/parameter reads over that merged list once per duplicate.
        /// </remarks>
        public List<CircuitData> GetFilteredCircuits(Document doc)
        {
            List<CircuitData> circuitDataList = new List<CircuitData>();

            try
            {
                FilteredElementCollector circuitCollector = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem))
                    .OfCategory(BuiltInCategory.OST_ElectricalCircuit);

                foreach (ElectricalSystem circuit in circuitCollector)
                {
                    try
                    {
                        CircuitData data = BuildCircuitData(circuit, requireRemotePowerSupply: true);
                        if (data != null)
                            circuitDataList.Add(data);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("TurboSuite Error",
                    $"Error collecting circuits:\n{ex.Message}");
            }

            return circuitDataList;
        }

        /// <summary>
        /// Build <see cref="CircuitData"/> from a circuit's actual membership. When
        /// <paramref name="requireRemotePowerSupply"/> is true, returns null unless at least one
        /// member fixture has the Remote Power Supply type parameter checked.
        /// </summary>
        private CircuitData BuildCircuitData(ElectricalSystem circuit, bool requireRemotePowerSupply)
        {
            var data = new CircuitData
            {
                CircuitId = circuit.Id,
                CircuitNumber = ParameterHelper.GetCircuitNumber(circuit),
                LoadName = ParameterHelper.GetLoadName(circuit),
                NumberOfElements = 0,
                ApparentPower = ParameterHelper.GetApparentLoad(circuit),
                Panel = ParameterHelper.GetPanelName(circuit)
            };

            bool hasRps = false;

            if (circuit.Elements != null)
            {
                foreach (Element el in circuit.Elements)
                {
                    if (el is not FamilyInstance fi)
                        continue;

                    try
                    {
                        if (fi.Category?.BuiltInCategory == BuiltInCategory.OST_LightingFixtures)
                        {
                            data.LightingFixtures.Add(CreateFixtureData(fi));
                            if (ParameterHelper.HasRemotePowerSupply(fi))
                                hasRps = true;
                        }
                        else if (fi.Category?.BuiltInCategory == BuiltInCategory.OST_LightingDevices)
                        {
                            var deviceData = CreateDeviceData(fi);
                            if (!data.DevicesByType.TryGetValue(deviceData.CurrentFamilyTypeName, out var typeList))
                            {
                                typeList = new List<DeviceData>();
                                data.DevicesByType[deviceData.CurrentFamilyTypeName] = typeList;
                            }
                            typeList.Add(deviceData);
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }
            }

            if (requireRemotePowerSupply && !hasRps)
                return null;

            data.NumberOfElements = data.LightingFixtures.Count +
                                    data.DevicesByType.Values.Sum(list => list.Count);
            return data;
        }

        /// <summary>
        /// Build CircuitData for a single pre-selected electrical circuit.
        /// No "Remote Power Supply" filter — the user explicitly chose this circuit.
        /// </summary>
        public CircuitData GetCircuitData(Document doc, ElectricalSystem circuit)
        {
            return BuildCircuitData(circuit, requireRemotePowerSupply: false);
        }

        /// <summary>
        /// Get the Switch ID for a circuit from existing devices or fixtures.
        /// </summary>
        public static string GetCircuitSwitchId(Document doc, CircuitData data)
        {
            // Try existing devices first
            foreach (var kvp in data.DevicesByType)
            {
                foreach (var device in kvp.Value)
                {
                    if (!string.IsNullOrWhiteSpace(device.SwitchID))
                        return device.SwitchID;
                }
            }

            // Fall back to reading Switch ID from fixtures
            foreach (var fixture in data.LightingFixtures)
            {
                var element = doc.GetElement(fixture.FixtureId.ToElementId());
                if (element != null)
                {
                    string switchId = ParameterHelper.GetSwitchID(element);
                    if (!string.IsNullOrWhiteSpace(switchId))
                        return switchId;
                }
            }

            return string.Empty;
        }

        private FixtureData CreateFixtureData(FamilyInstance element)
        {
            return new FixtureData
            {
                FixtureId = element.Id.ToRef(),
                TypeMark = ParameterHelper.GetTypeMark(element),
                Comments = ParameterHelper.GetComments(element),
                LinearLength = ParameterHelper.GetLinearLength(element),
                LinearPower = ParameterHelper.GetLinearPower(element),
                TypePower = ParameterHelper.GetDriverPower(element.Symbol),
                Manufacturer = ParameterHelper.GetManufacturer(element),
                DimmingProtocol = ParameterHelper.GetDimmingProtocol(element),
                Voltage = ParameterHelper.GetVoltage(element),
                HasRemotePowerSupply = ParameterHelper.HasRemotePowerSupply(element)
            };
        }

        private DeviceData CreateDeviceData(FamilyInstance instance)
        {
            string typeName = instance?.Symbol?.Name ?? "Unknown";
            ElementId typeId = instance?.Symbol?.Id ?? ElementId.InvalidElementId;

            return new DeviceData
            {
                DeviceId = instance.Id,
                SwitchID = ParameterHelper.GetSwitchID(instance),
                CurrentFamilyTypeId = typeId,
                CurrentFamilyTypeName = typeName,
                // A LightingDevice whose type carries DMX Channels > 0 is a DMX decoder (TurboRPS-2).
                DmxChannels = ReadDmxChannels(instance, instance?.Symbol)
            };
        }

        /// <summary>Read the integer "DMX Channels" value, preferring the instance binding and falling
        /// back to the type — the same convention the TurboDMX model reader uses. Returns 0 when absent.</summary>
        private static int ReadDmxChannels(Element instance, FamilySymbol symbol)
        {
            var p = instance?.LookupParameter(DmxParameterNames.DmxChannels);
            if (p == null || !p.HasValue)
                p = symbol?.LookupParameter(DmxParameterNames.DmxChannels);
            if (p == null || !p.HasValue)
                return 0;
            return p.StorageType switch
            {
                StorageType.Integer => p.AsInteger(),
                StorageType.Double => (int)Math.Round(p.AsDouble()),
                _ => 0
            };
        }
    }
}
