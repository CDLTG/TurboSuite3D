#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
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
        /// Get all electrical circuits that have at least one Lighting Fixture with Remote Power Supply checked
        /// </summary>
	public List<CircuitData> GetFilteredCircuits(Document doc)
        {
            List<CircuitData> circuitDataList = new List<CircuitData>();

            try
            {
                FilteredElementCollector deviceCollector = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_LightingDevices)
                    .OfClass(typeof(FamilyInstance));

                Dictionary<string, List<FamilyInstance>> devicesByCircuit = new Dictionary<string, List<FamilyInstance>>();

                foreach (FamilyInstance device in deviceCollector)
                {
                    try
                    {
                        Parameter circuitParam = device.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER);
                        string circuitNumber = circuitParam?.AsString();

                        if (string.IsNullOrWhiteSpace(circuitNumber))
                            continue;

                        if (!devicesByCircuit.TryGetValue(circuitNumber, out var deviceList))
                        {
                            deviceList = new List<FamilyInstance>();
                            devicesByCircuit[circuitNumber] = deviceList;
                        }
                        deviceList.Add(device);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                }

                FilteredElementCollector fixtureCollector = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_LightingFixtures)
                    .OfClass(typeof(FamilyInstance));

                Dictionary<string, List<FamilyInstance>> fixturesByCircuit = new Dictionary<string, List<FamilyInstance>>();
                HashSet<string> qualifyingCircuits = new HashSet<string>();

                foreach (FamilyInstance fixture in fixtureCollector)
                {
                    try
                    {
                        Parameter circuitParam = fixture.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER);
                        string circuitNumber = circuitParam?.AsString();

                        if (string.IsNullOrWhiteSpace(circuitNumber))
                            continue;

                        if (!fixturesByCircuit.TryGetValue(circuitNumber, out var fixtureList))
                        {
                            fixtureList = new List<FamilyInstance>();
                            fixturesByCircuit[circuitNumber] = fixtureList;
                        }
                        fixtureList.Add(fixture);

                        if (ParameterHelper.HasRemotePowerSupply(fixture))
                            qualifyingCircuits.Add(circuitNumber);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                }

                FilteredElementCollector circuitCollector = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem))
                    .OfCategory(BuiltInCategory.OST_ElectricalCircuit);

                foreach (ElectricalSystem circuit in circuitCollector)
                {
                    try
                    {
                        string circuitNumber = ParameterHelper.GetCircuitNumber(circuit);

                        if (!qualifyingCircuits.Contains(circuitNumber))
                            continue;

                        CircuitData data = new CircuitData
                        {
                            CircuitId = circuit.Id,
                            CircuitNumber = circuitNumber,
                            LoadName = ParameterHelper.GetLoadName(circuit),
                            LoadClassificationAbbreviation = ParameterHelper.GetLoadClassification(circuit),
                            NumberOfElements = 0,
                            ApparentPower = ParameterHelper.GetApparentLoad(circuit),
                            Panel = ParameterHelper.GetPanelName(circuit)
                        };

                        if (fixturesByCircuit.TryGetValue(circuitNumber, out var circuitFixtures))
                        {
                            foreach (FamilyInstance fixture in circuitFixtures)
                            {
                                try
                                {
                                    FixtureData fixtureData = CreateFixtureData(fixture);
                                    data.LightingFixtures.Add(fixtureData);
                                }
                                catch
                                {
                                    continue;
                                }
                            }
                        }

                        if (devicesByCircuit.TryGetValue(circuitNumber, out var circuitDevices))
                        {
                            foreach (FamilyInstance device in circuitDevices)
                            {
                                try
                                {
                                    DeviceData deviceData = CreateDeviceData(device);

                                    if (!data.DevicesByType.TryGetValue(deviceData.CurrentFamilyTypeName, out var typeList))
                                    {
                                        typeList = new List<DeviceData>();
                                        data.DevicesByType[deviceData.CurrentFamilyTypeName] = typeList;
                                    }
                                    typeList.Add(deviceData);
                                }
                                catch
                                {
                                    continue;
                                }
                            }
                        }

                        data.NumberOfElements = data.LightingFixtures.Count +
                                                data.DevicesByType.Values.Sum(list => list.Count);

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
                Autodesk.Revit.UI.TaskDialog.Show("Error",
                    $"Error collecting circuits:\n{ex.Message}");
            }

            return circuitDataList;
        }

        /// <summary>
        /// Build CircuitData for a single pre-selected electrical circuit.
        /// No "Remote Power Supply" filter — the user explicitly chose this circuit.
        /// </summary>
        public CircuitData GetCircuitData(Document doc, ElectricalSystem circuit)
        {
            var data = new CircuitData
            {
                CircuitId = circuit.Id,
                CircuitNumber = ParameterHelper.GetCircuitNumber(circuit),
                LoadName = ParameterHelper.GetLoadName(circuit),
                LoadClassificationAbbreviation = ParameterHelper.GetLoadClassification(circuit),
                NumberOfElements = 0,
                ApparentPower = ParameterHelper.GetApparentLoad(circuit),
                Panel = ParameterHelper.GetPanelName(circuit)
            };

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

            data.NumberOfElements = data.LightingFixtures.Count +
                                    data.DevicesByType.Values.Sum(list => list.Count);
            return data;
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
                var element = doc.GetElement(fixture.FixtureId);
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
                FixtureId = element.Id,
                TypeMark = ParameterHelper.GetTypeMark(element),
                Comments = ParameterHelper.GetComments(element),
                LinearLength = ParameterHelper.GetLinearLength(element),
                LinearPower = ParameterHelper.GetLinearPower(element),
                TypePower = ParameterHelper.GetDriverPower(element.Symbol),
                Manufacturer = ParameterHelper.GetManufacturer(element),
                DimmingProtocol = ParameterHelper.GetDimmingProtocol(element),
                Voltage = ParameterHelper.GetVoltage(element)
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
                CurrentFamilyTypeName = typeName
            };
        }
    }
}
