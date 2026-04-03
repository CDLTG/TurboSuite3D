using System;
using System.Collections.Generic;

namespace TurboSuite.Docs.Models;

public class LoadsCircuitModel
{
    public string CircuitNumber { get; set; } = string.Empty;
    public string LoadName { get; set; } = string.Empty;
    public string LoadClassification { get; set; } = string.Empty;
    public double ApparentLoadVA { get; set; }
    public string TotalWattsDisplay => $"{Math.Round(ApparentLoadVA)} VA";
    public List<LoadsFixtureGroup> FixtureGroups { get; set; } = new();
}

public class LoadsFixtureGroup
{
    public string TypeMark { get; set; } = string.Empty;
    public bool IsLinear { get; set; }
    public int Quantity { get; set; }
    public double TotalLinearLengthFeet { get; set; }
}
