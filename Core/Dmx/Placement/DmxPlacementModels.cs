#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx.Placement
{
    // Pure, Revit-free placement plan + result ( "Placement + tags"). The planner walks a
    // solved DmxBill into an ordered, loop-by-loop list of devices to drop — each a decoder + its driver,
    // carrying the FamilySymbol identities (UniqueId) the shim resolves and the global "DEC n" Switch ID
    // that matches the bill's decoder numbering. The shim consumes this on the Revit thread (click-to-place);
    // nothing here touches Revit, so the numbering is unit-testable in isolation.

    /// <summary>One decoder+driver to place: the resolved family identities, the decoder's Switch ID, and
    /// the type names (for warnings when a name doesn't resolve to a loaded symbol).</summary>
    public sealed class DmxDevicePlacement
    {
        public DmxDevicePlacement(string switchId,
                                  string? decoderTypeId, string decoderName,
                                  string? driverTypeId, string driverName,
                                  string? zoneName = null)
        {
            SwitchId = switchId;
            DecoderTypeId = decoderTypeId;
            DecoderName = decoderName;
            DriverTypeId = driverTypeId;
            DriverName = driverName;
            ZoneName = zoneName ?? "";
        }

        /// <summary>The decoder number written to <c>Switch ID</c> and shown by its tag, e.g. "DEC 7".</summary>
        public string SwitchId { get; }

        /// <summary>FamilySymbol UniqueId of the chosen decoder type, or null if its name didn't resolve.</summary>
        public string? DecoderTypeId { get; }
        public string DecoderName { get; }

        /// <summary>FamilySymbol UniqueId of the chosen driver type, or null if its name didn't resolve.</summary>
        public string? DriverTypeId { get; }
        public string DriverName { get; }

        /// <summary>The Control Zone this decoder serves — the grain the shim circuits on (one
        /// <c>&lt;unnamed&gt;</c> circuit per Control Zone: all its DMX fixtures + all its decoders + drivers).
        /// A zone's several decoders are power subdivision under its one control address.</summary>
        public string ZoneName { get; }
    }

    /// <summary>One loop (= one interface) worth of devices, placed at a single picked point.</summary>
    public sealed class DmxLoopPlacement
    {
        public DmxLoopPlacement(int interfaceNumber, string? loopName, IReadOnlyList<DmxDevicePlacement> devices)
        {
            InterfaceNumber = interfaceNumber;
            LoopName = loopName;
            Devices = devices;
        }

        public int InterfaceNumber { get; }

        /// <summary>The declared loop name, or null for an auto-packed interface.</summary>
        public string? LoopName { get; }

        public IReadOnlyList<DmxDevicePlacement> Devices { get; }

        /// <summary>Human label for the pick prompt, e.g. "loop \"House\"" or "interface #2 (auto-packed)".</summary>
        public string Label => LoopName != null
            ? $"loop \"{LoopName}\""
            : $"interface #{InterfaceNumber} (auto-packed)";
    }

    /// <summary>A placed decoder+driver pair's identity — the DEC # and the two element ids (as the
    /// Revit-free long the shim converts). Persisted in the placement registry so a later re-Place can delete
    /// an orphaned decoder AND its paired driver exactly, even after the layout's been nudged.</summary>
    public sealed class DmxPlacedPair
    {
        public DmxPlacedPair(int dec, long decoderId, long driverId)
        {
            Dec = dec;
            DecoderId = decoderId;
            DriverId = driverId;
        }

        public int Dec { get; }
        public long DecoderId { get; }
        public long DriverId { get; }   // 0 ⇒ no paired driver was placed
    }

    /// <summary>The full system's placement plan — its loops, in bill order.</summary>
    public sealed class DmxPlacementPlan
    {
        public DmxPlacementPlan(IReadOnlyList<DmxLoopPlacement> loops) => Loops = loops;

        public IReadOnlyList<DmxLoopPlacement> Loops { get; }

        public int LoopCount => Loops.Count;
        public int DeviceCount => Loops.Sum(l => l.Devices.Count);
    }

    /// <summary>Outcome of a placement run, surfaced back to the window after the work-queue write.</summary>
    public sealed class DmxPlacementResult
    {
        public int DecodersPlaced { get; set; }
        public int DriversPlaced { get; set; }
        public int SwitchIdsSet { get; set; }
        public int TagsPlaced { get; set; }
        public int Failed { get; set; }

        /// <summary>Decoders skipped because their DEC # is already in the model — re-Place lands only the
        /// unbuilt remainder, so it's safe to Place again after a locked additive re-run.</summary>
        public int AlreadyPlaced { get; set; }

        /// <summary>Orphaned decoders removed this run — DEC #s no longer in the solve (Option A cleanup).</summary>
        public int RemovedDecoders { get; set; }

        /// <summary>Per-zone electrical circuits created this run (§2/§3) — one <c>&lt;unnamed&gt;</c> power
        /// circuit per Control Zone (its DMX fixtures + decoders + drivers).</summary>
        public int CircuitsCreated { get; set; }

        /// <summary>Stale/changed/orphaned circuits torn down this run (§3 two-phase reconcile).</summary>
        public int CircuitsRemoved { get; set; }

        /// <summary>True when the designer pressed Escape before placing every loop (partial placement kept).</summary>
        public bool Cancelled { get; set; }

        public List<string> Warnings { get; } = new List<string>();

        /// <summary>Pairs newly placed this run — folded into the persisted registry by the ViewModel.</summary>
        public List<DmxPlacedPair> PlacedPairs { get; } = new List<DmxPlacedPair>();

        /// <summary>DEC #s removed as orphans this run — pruned from the persisted registry by the ViewModel.</summary>
        public List<int> RemovedDecs { get; } = new List<int>();

        /// <summary>One-line summary for the window's status strip.</summary>
        public string Summary =>
            (Cancelled ? "Placement cancelled — " : "Placed ")
            + $"{DecodersPlaced} decoder(s) + {DriversPlaced} driver(s); "
            + $"{SwitchIdsSet} Switch ID(s), {TagsPlaced} tag(s)"
            + (AlreadyPlaced > 0 ? $"; {AlreadyPlaced} already placed (skipped)" : "")
            + (RemovedDecoders > 0 ? $"; {RemovedDecoders} orphan(s) removed" : "")
            + (CircuitsCreated > 0 ? $"; {CircuitsCreated} circuit(s) created" : "")
            + (CircuitsRemoved > 0 ? $"; {CircuitsRemoved} circuit(s) torn down" : "")
            + (Failed > 0 ? $"; {Failed} failed" : "")
            + (Warnings.Count > 0 ? $" ({Warnings.Count} warning(s))" : "") + ".";
    }
}
