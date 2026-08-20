#nullable enable
using System.Collections.Generic;
using TurboSuite.Dmx.Lock;

namespace TurboSuite.Dmx.Placement
{
    /// <summary>
    /// Turns a solved <see cref="DmxBill"/> + its reconciled <see cref="DmxNumbering"/> into a
    /// <see cref="DmxPlacementPlan"/> (3). Walks the bill in the SAME order the numbering
    /// was assigned — interfaces → their zones → each zone's clusters → powered decoders — and stamps each
    /// decoder's <c>Switch ID</c> ("DEC n") from the numbering, so placed numbers match the bill AND honor
    /// the lock baseline. Each device carries the chosen decoder/driver type names mapped back to
    /// loaded-family identities (UniqueId) via the curated-pool maps; an unmapped name yields a null id (the
    /// shim places nothing for it and warns), never an exception.
    /// </summary>
    public static class DmxPlacementPlanner
    {
        public static DmxPlacementPlan Build(
            DmxBill bill,
            DmxNumbering numbering,
            IReadOnlyDictionary<string, string> decoderNameToTypeId,
            IReadOnlyDictionary<string, string> driverNameToTypeId)
        {
            var byZone = new Dictionary<string, ZoneSolution>();
            foreach (var z in bill.Zones) byZone[z.ZoneName] = z;

            var loops = new List<DmxLoopPlacement>(bill.Interfaces.Count);

            foreach (var iface in bill.Interfaces)
            {
                var devices = new List<DmxDevicePlacement>();
                foreach (var addressed in iface.Interface.Zones)
                {
                    if (!byZone.TryGetValue(addressed.ZoneName, out var sol)) continue;
                    numbering.DecIdsByZone.TryGetValue(addressed.ZoneName, out var decIds);

                    int idx = 0;   // index into this zone's DEC #s, in pack order
                    foreach (var cluster in sol.Clusters)
                    foreach (var pd in cluster.Power.Decoders)
                    {
                        int dec = decIds != null && idx < decIds.Count ? decIds[idx] : 0;
                        idx++;
                        decoderNameToTypeId.TryGetValue(sol.Decoder.Name, out var decoderId);
                        driverNameToTypeId.TryGetValue(pd.Driver.Name, out var driverId);
                        devices.Add(new DmxDevicePlacement(
                            switchId: $"DEC {dec}",
                            decoderTypeId: decoderId, decoderName: sol.Decoder.Name,
                            driverTypeId: driverId, driverName: pd.Driver.Name,
                            zoneName: addressed.ZoneName));   // circuiting grain: one circuit per Control Zone
                    }
                }

                loops.Add(new DmxLoopPlacement(iface.Interface.InterfaceNumber, iface.Interface.LoopName, devices));
            }

            return new DmxPlacementPlan(loops);
        }
    }
}
