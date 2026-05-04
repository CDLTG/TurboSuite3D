using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Docs.Models;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Docs.Services;

public static class LoadsCollectorService
{
    public static List<LoadsCircuitModel> Collect(Document doc)
    {
        var results = new List<LoadsCircuitModel>();

        var circuits = new FilteredElementCollector(doc)
            .OfClass(typeof(ElectricalSystem))
            .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
            .Cast<ElectricalSystem>();

        foreach (var circuit in circuits)
        {
            try
            {
                string circuitNumber = ParameterHelper.GetCircuitNumber(circuit);
                if (string.IsNullOrWhiteSpace(circuitNumber)) continue;
                if (circuitNumber.Contains("Feed Through Lugs", StringComparison.OrdinalIgnoreCase)) continue;

                var fixtureGroups = new List<LoadsFixtureGroup>();
                var driverSwitchIds = new List<string>();

                if (circuit.Elements != null)
                {
                    var fixtureData = new List<(string TypeMark, double LinearLength)>();

                    foreach (Element el in circuit.Elements)
                    {
                        if (el is not FamilyInstance fi) continue;

                        // Collect fixture TypeMark/LinearLength from Lighting Fixtures and Electrical Fixtures
                        if (fi.Category?.BuiltInCategory is BuiltInCategory.OST_LightingFixtures
                            or BuiltInCategory.OST_ElectricalFixtures)
                        {
                            string typeMark = ParameterHelper.GetTypeMark(fi);
                            if (!string.IsNullOrWhiteSpace(typeMark))
                            {
                                double linearLength = ParameterHelper.GetLinearLength(fi);
                                fixtureData.Add((typeMark, linearLength));
                            }
                        }

                        // Collect Switch IDs from Lighting Devices (remote power supplies)
                        if (fi.Category?.BuiltInCategory == BuiltInCategory.OST_LightingDevices)
                        {
                            string switchId = ParameterHelper.GetSwitchID(fi);
                            if (!string.IsNullOrWhiteSpace(switchId))
                                driverSwitchIds.Add(switchId);
                        }
                    }

                    fixtureGroups = fixtureData
                        .GroupBy(f => f.TypeMark, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new LoadsFixtureGroup
                        {
                            TypeMark = g.Key,
                            IsLinear = g.Any(f => f.LinearLength > 0),
                            Quantity = g.Count(),
                            TotalLinearLengthFeet = g.Sum(f => f.LinearLength)
                        })
                        .OrderBy(g => g.TypeMark, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                results.Add(new LoadsCircuitModel
                {
                    CircuitNumber = circuitNumber,
                    LoadName = ParameterHelper.GetLoadName(circuit),
                    LoadClassification = ParameterHelper.GetLoadClassification(circuit),
                    ApparentLoadVA = ParameterHelper.GetApparentLoad(circuit),
                    FixtureGroups = fixtureGroups,
                    DriverSwitchIDs = driverSwitchIds
                });
            }
            catch
            {
                continue;
            }
        }

        results.Sort((a, b) => NaturalStringComparer.OrdinalIgnoreCase.Compare(a.CircuitNumber, b.CircuitNumber));
        return results;
    }
}
