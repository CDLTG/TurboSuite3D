using System.Collections.Generic;
using TurboSuite.Driver.Models;

namespace TurboSuite.Docs.Models;

/// <summary>
/// One RPS circuit's driver/sub-driver breakdown, as rendered by <see cref="Services.RPSBreakdownPdfService"/>.
/// A Revit-free projection of the TurboRPS dashboard's per-circuit recommendation
/// (<see cref="RpsCircuitData"/>) — the recommended driver type × qty plus every packed
/// sub-driver channel. Reuses the Driver engine's <see cref="SubDriverAssignment"/>/
/// <see cref="FixtureSegment"/> models verbatim so the packing shown here is identical to
/// what TurboRPS displays.
/// </summary>
public class RPSBreakdownModel
{
    public string CircuitNumber { get; set; } = string.Empty;
    public string LoadName { get; set; } = string.Empty;

    /// <summary>Comma-joined placed-driver Switch IDs (e.g. "X07a, X07b"); empty when none placed.</summary>
    public string SwitchIds { get; set; } = string.Empty;

    /// <summary>Recommended driver family-type display name (e.g. "AL_Juniper_Driver : 192W").</summary>
    public string RecommendedType { get; set; } = string.Empty;

    /// <summary>Recommended number of physical drivers.</summary>
    public int DriverCount { get; set; }

    /// <summary>Total wattage of the RPS fixtures this circuit's driver serves.</summary>
    public double TotalLoadWatts { get; set; }

    /// <summary>The packed sub-driver channels, in order.</summary>
    public List<SubDriverAssignment> SubDrivers { get; set; } = new();

    /// <summary>The circuit's RPS fixtures, grouped for the right-hand "Fixtures" list —
    /// mirrors the TurboRPS detail pane's grouped-fixtures table.</summary>
    public List<BreakdownFixture> Fixtures { get; set; } = new();
}

/// <summary>One grouped fixture line (identical type + comments + length collapsed to a qty),
/// as shown in the breakdown's per-circuit Fixtures list.</summary>
public class BreakdownFixture
{
    public int Quantity { get; set; }
    public string TypeMark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public double LinearLength { get; set; }
}
