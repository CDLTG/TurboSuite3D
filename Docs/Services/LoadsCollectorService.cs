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

                var fixtureGroups = new List<LoadsFixtureGroup>();

                if (circuit.Elements != null)
                {
                    var fixtureData = new List<(string TypeMark, double LinearLength)>();

                    foreach (Element el in circuit.Elements)
                    {
                        if (el is not FamilyInstance fi) continue;
                        if (fi.Category?.BuiltInCategory != BuiltInCategory.OST_LightingFixtures) continue;

                        string typeMark = ParameterHelper.GetTypeMark(fi);
                        if (string.IsNullOrWhiteSpace(typeMark)) continue;

                        double linearLength = ParameterHelper.GetLinearLength(fi);
                        fixtureData.Add((typeMark, linearLength));
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
                    FixtureGroups = fixtureGroups
                });
            }
            catch
            {
                continue;
            }
        }

        results.Sort((a, b) => string.Compare(a.CircuitNumber, b.CircuitNumber, StringComparison.OrdinalIgnoreCase));
        return results;
    }
}
