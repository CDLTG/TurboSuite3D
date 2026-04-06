#nullable disable
using System.Collections.Generic;
using TurboSuite.Zones.Models;

namespace TurboSuite.Docs.Models;

public class PanelScheduleData
{
    public PanelAllocationResult Allocation { get; set; }
    public Dictionary<string, ZonesCircuitData> CircuitLookup { get; set; }
    public BrandConfig Brand { get; set; }
}
