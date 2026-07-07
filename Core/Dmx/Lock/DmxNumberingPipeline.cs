#nullable enable
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx.Persistence;

namespace TurboSuite.Dmx.Lock
{
    /// <summary>Flattens a solved <see cref="DmxBill"/> into the canonical-order zone list the reconciler
    /// numbers — the SAME walk (interfaces → their zones) the placement planner uses, so DEC #s and Switch
    /// IDs line up.</summary>
    public static class DmxBillFlattener
    {
        public static List<DmxSolvedZone> Flatten(DmxBill bill)
        {
            var byZone = new Dictionary<string, ZoneSolution>();
            foreach (var z in bill.Zones) byZone[z.ZoneName] = z;

            var result = new List<DmxSolvedZone>();
            foreach (var iface in bill.Interfaces)
                foreach (var az in iface.Interface.Zones)
                    if (byZone.TryGetValue(az.ZoneName, out var sol))
                        result.Add(new DmxSolvedZone(az.ZoneName, iface.Interface.InterfaceNumber,
                                                     sol.Decoder.Name, sol.DecoderCount));
            return result;
        }
    }

    /// <summary>Captures a numbering as the frozen lock baseline (Lock / Re-lock event).</summary>
    public static class DmxSnapshotBuilder
    {
        public static DmxSnapshotDto Capture(DmxNumbering numbering, string state = "Locked") =>
            new DmxSnapshotDto
            {
                NumberingState = state,
                Zones = numbering.Zones.Select(z => new DmxSnapshotZoneDto
                {
                    ZoneValue = z.ZoneValue,
                    InterfaceNumber = z.InterfaceNumber,
                    DecoderType = z.DecoderType,
                    DecIds = z.DecIds.ToList(),
                }).ToList(),
            };
    }
}
