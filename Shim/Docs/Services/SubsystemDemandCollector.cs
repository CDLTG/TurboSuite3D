#nullable disable
using System.Collections.Generic;
using Autodesk.Revit.DB;
using TurboSuite.Dali.Services;
using TurboSuite.Dmx.Services;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;

namespace TurboSuite.Docs.Services;

/// <summary>
/// The control subsystems that report their own hardware (DMX, shades, DALI), gathered once so every
/// TurboDocs surface that re-derives the panel breakdown — the Control BOM and the Panel Schedule — feeds
/// <c>BuildPanelBreakdown</c> the identical demand set and cannot disagree about what a subsystem accounts
/// for. It is the single list the TurboZones window builds too (<c>ZonesCommand</c>), so the issued PDFs
/// and the live breakdown stay in lockstep.
///
/// A provider never throws; a subsystem that cannot solve returns a diagnostic instead of parts, which an
/// issued document drops rather than prints. Add a fourth provider here and both surfaces pick it up.
/// </summary>
public static class SubsystemDemandCollector
{
    public static List<ControlSubsystemDemand> Collect(Document doc) =>
        new List<ControlSubsystemDemand>
        {
            new DmxDemandProvider(doc).GetDemand(),
            new ShadeDemandProvider(doc).GetDemand(),
            new DaliDemandProvider(doc).GetDemand()
        };
}
