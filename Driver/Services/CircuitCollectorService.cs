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
                // Step 1: Collect ALL Lighting Devices in the project
                FilteredElementCollector deviceCollector = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_LightingDevices)
                    .OfClass(typeof(FamilyInstance));

                // Step 2: Group devices by circuit number
                Dictionary<string, List<FamilyInstance>> devicesByCircuit = new Dictionary<string, List<FamilyInstance>>();

                foreach (FamilyInstance device in deviceCollector)
                {
                    try
                    {
                        Parameter circuitParam = device.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER);
                        string circuitNumber = circuitParam?.AsString();

                        if (string.IsNullOrWhiteSpace(circuitNumber))
                            continue;

                        if (!devicesByCircuit.ContainsKey(circuitNumber))
                        {
                            devicesByCircuit[circuitNumber] = new List<FamilyInstance>();
                        }
                        devicesByCircuit[circuitNumber].Add(device);
                    }
                    catch
                    {
                        continue;
                    }
                }

                // Step 3: Collect ALL Lighting Fixtures in the project
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

                        if (!fixturesByCircuit.ContainsKey(circuitNumber))
                        {
                            fixturesByCircuit[circuitNumber] = new List<FamilyInstance>();
                        }
                        fixturesByCircuit[circuitNumber].Add(fixture);

                        if (ParameterHelper.HasRemotePowerSupply(fixture))
                            qualifyingCircuits.Add(circuitNumber);
                    }
                    catch
                    {
                        continue;
                    }
                }

                // Step 4: Get all electrical circuits
                FilteredElementCollector circuitCollector = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem))
                    .OfCategory(BuiltInCategory.OST_ElectricalCircuit);

                // Step 5: Create CircuitData for circuits that have fixtures with Remote Power Supply
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

                        if (fixturesByCircuit.ContainsKey(circuitNumber))
                        {
                            foreach (FamilyInstance fixture in fixturesByCircuit[circuitNumber])
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

                        if (devicesByCircuit.ContainsKey(circuitNumber))
                        {
                            foreach (FamilyInstance device in devicesByCircuit[circuitNumber])
                            {
                                try
                                {
                                    DeviceData deviceData = CreateDeviceData(device);

                                    if (!data.DevicesByType.ContainsKey(deviceData.CurrentFamilyTypeName))
                                    {
                                        data.DevicesByType[deviceData.CurrentFamilyTypeName] = new List<DeviceData>();
                                    }
                                    data.DevicesByType[deviceData.CurrentFamilyTypeName].Add(deviceData);
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
                    catch
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
