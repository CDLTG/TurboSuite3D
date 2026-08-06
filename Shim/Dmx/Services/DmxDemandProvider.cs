#nullable enable
using System.Collections.Generic;
using Autodesk.Revit.DB;
using TurboSuite.Dmx.Input;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;

namespace TurboSuite.Dmx.Services
{
    /// <summary>
    /// TurboDMX as a control-subsystem demand provider: reads the model and the module's persisted
    /// design, re-solves headlessly, and reports the QSE-CI-DMX interfaces the job needs.
    ///
    /// This is the whole point of the seam. The interface count is real channel math — 32 channels
    /// each, packed under the profile's caps — and TurboDMX is the only thing that knows it. Before
    /// this, the control BOM took the count from a hand-picked compartment dropdown, so a job could
    /// order one interface while TurboDMX's own bill called for four.
    /// </summary>
    public sealed class DmxDemandProvider : IControlSubsystemDemandProvider
    {
        /// <summary>The subsystem name the BOM and the panel breakdown label this demand with.</summary>
        public const string SubsystemName = "DMX";

        /// <summary>The Lutron QS DMX interface. Matches the <c>"DMX"</c> entry in
        /// <see cref="BrandConfig"/>'s special devices, so the demanded part and the hand-placed one
        /// are the same line rather than two.</summary>
        private const string InterfacePartNumber = "QSE-CI-DMX";

        private readonly Document _doc;

        public DmxDemandProvider(Document doc) => _doc = doc;

        public ControlSubsystemDemand GetDemand()
        {
            if (_doc == null) return ControlSubsystemDemand.None(SubsystemName);

            var snapshot = new DmxModelReader(_doc).Read();
            var state = DmxStorageService.Load(_doc);

            var result = DmxHeadlessSolve.Solve(snapshot, state);

            // Nothing solved. Either a clean nothing (no DMX in the job) or a reason — the result
            // carries whichever, and both are correct answers here.
            if (result.Bill == null || result.Bill.InterfaceCount == 0)
                return new ControlSubsystemDemand(
                    SubsystemName, servedZones: result.ZoneNames, diagnostic: result.Diagnostic);

            // A bill AND a diagnostic is a real combination, not a contradiction: a solve over
            // partially zoned tape is complete for what it saw and still under-counts the job. Order
            // the parts, and carry the caveat with them.
            var bill = result.Bill;

            // The QS-link accounting, from the QSE-CI-DMX submittal: each interface "counts as 1 QS
            // device and 0 zones", and "each DMX channel = 1 switch leg". Devices and legs are
            // therefore independent budgets, not two views of the same number — an interface with 4
            // channels costs one device and four legs.
            return new ControlSubsystemDemand(
                SubsystemName,
                parts: new List<DemandPart>
                {
                    new DemandPart(InterfacePartNumber, bill.InterfaceCount, DemandMount.LvCompartment)
                },
                linkDevices: bill.InterfaceCount,
                linkLoads: bill.TotalChannels,
                servedZones: result.ZoneNames,
                diagnostic: result.Diagnostic);
        }
    }
}
