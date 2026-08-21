#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Dali.Addressing;
using TurboSuite.Dali.Input;
using TurboSuite.Driver.Services;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Dali.Services
{
    /// <summary>One addressable unit paired with the live element to write its address onto — the driver
    /// device (driver unit) or the self-driven fixture (downlight unit). Core holds the Revit-free
    /// <see cref="Reading"/>; the shim keeps the <see cref="WriteTargetId"/> it can't.</summary>
    public sealed class DaliUnitEntry
    {
        public DaliUnitEntry(DaliUnitReading reading, ElementId writeTargetId)
        {
            Reading = reading;
            WriteTargetId = writeTargetId;
        }

        public DaliUnitReading Reading { get; }
        public ElementId WriteTargetId { get; }
    }

    /// <summary>The result of one model walk: every addressable unit (with its write target) plus non-fatal
    /// warnings (e.g. a circuit whose drivers share a Switch ID suffix).</summary>
    public sealed class DaliUnitScan
    {
        public DaliUnitScan(IReadOnlyList<DaliUnitEntry> entries, IReadOnlyList<string> warnings)
        {
            Entries = entries;
            Warnings = warnings;
        }

        public IReadOnlyList<DaliUnitEntry> Entries { get; }
        public IReadOnlyList<string> Warnings { get; }
    }

    /// <summary>
    /// The <b>single shared model walk</b> that enumerates DALI <see cref="DaliUnitReading"/>s — one
    /// addressable unit per DALI address. The demand/counting side
    /// (<c>DaliDemandProvider.CountDaliLoadsByZone</c>), the addressing reader (<c>DaliModelReader</c>) and the
    /// writer (<c>DaliAddressWriter</c>) all consume this one enumeration, so a zone's counted load total, its
    /// issued-address count, and its write targets cannot disagree.
    ///
    /// <para><b>Per-circuit either/or</b> (a circuit never mixes a driver device with an independently
    /// addressed self-driven fixture): a circuit carrying ≥1 DALI fixture is a DALI circuit; if it also carries
    /// ≥1 <i>valid driver device</i> (<see cref="FamilyTypeCollectorService.GetDriverCandidates"/> →
    /// <c>IsValidDriver</c>, which already excludes keypads/sensors/DMX decoders) its units are <b>those
    /// drivers</b> (one address each; the tape fixtures carry none); otherwise its units are its
    /// <b>self-driven downlight fixtures</b> (one address each). Uncircuited DALI fixtures are their own
    /// downlight units — conservative, until the designer wires them.</para>
    ///
    /// <para><b>Durable unit key.</b> Driver: <c>circuit.UniqueId#ordinal</c> — the circuit UniqueId is unique
    /// and survives a driver redeploy (the circuit is not recreated), and the ordinal is the driver's Switch ID
    /// suffix (<see cref="DaliDriverOrdinal"/>), the deterministic down-column index. The Switch ID <i>base</i>
    /// is a non-unique placeholder in real models, so only the suffix is used. If a circuit's drivers collide
    /// on an ordinal (blank/hand-edited Switch IDs), the colliding keys are made unique with an ElementId
    /// discriminator (not redeploy-stable) and a warning is raised — never a silent dropped address. Downlight:
    /// the fixture UniqueId.</para>
    /// </summary>
    public static class DaliUnitEnumerator
    {
        private const string DaliProtocol = "DALI";

        public static DaliUnitScan Scan(Document doc)
        {
            var entries = new List<DaliUnitEntry>();
            var warnings = new List<string>();
            if (doc == null) return new DaliUnitScan(entries, warnings);

            var lightingCatId = new ElementId(BuiltInCategory.OST_LightingFixtures);
            var validDriverSymbolIds = GetValidDriverSymbolIds(doc);
            var circuited = new HashSet<ElementId>();

            var circuits = new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem))
                .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
                .Cast<ElectricalSystem>();

            foreach (var circuit in circuits)
            {
                var daliFixtures = new List<FamilyInstance>();
                var drivers = new List<FamilyInstance>();
                var pts = new List<XYZ>();
                string circuitZone = "";

                foreach (Element el in circuit.Elements)
                {
                    if (!(el is FamilyInstance fi)) continue;

                    if (fi.Category?.Id == lightingCatId)
                    {
                        if (!IsDali(fi)) continue;
                        daliFixtures.Add(fi);
                        circuited.Add(fi.Id);
                        XYZ loc = GeometryHelper.GetFixtureLocation(fi);
                        if (loc != null) pts.Add(loc);
                        if (circuitZone.Length == 0)
                        {
                            string z = ReadZone(fi);
                            if (z.Length > 0) circuitZone = z;
                        }
                    }
                    else if (fi.Symbol != null && validDriverSymbolIds.Contains(fi.Symbol.Id))
                    {
                        drivers.Add(fi);
                    }
                }

                if (daliFixtures.Count == 0) continue;   // not a DALI circuit

                DaliPoint? centroid = pts.Count > 0
                    ? new DaliPoint(pts.Average(p => p.X), pts.Average(p => p.Y))
                    : (DaliPoint?)null;
                string circuitKey = circuit.UniqueId;

                if (drivers.Count > 0)
                    AddDriverUnits(entries, warnings, drivers, circuitKey, circuitZone, centroid);
                else
                    foreach (var fi in daliFixtures)   // self-driven downlights (decision #5: normally one)
                    {
                        XYZ loc = GeometryHelper.GetFixtureLocation(fi);
                        DaliPoint? here = loc != null ? new DaliPoint(loc.X, loc.Y) : centroid;
                        entries.Add(new DaliUnitEntry(
                            new DaliUnitReading("UID:" + fi.UniqueId, circuitKey, DaliUnitKind.Downlight,
                                                0, ReadZone(fi), here),
                            fi.Id));
                    }
            }

            // Uncircuited DALI fixtures — each its own downlight unit (conservative; collapses onto a circuit
            // the moment the designer wires it).
            var loose = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_LightingFixtures)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>();

            foreach (var fi in loose)
            {
                if (circuited.Contains(fi.Id) || !IsDali(fi)) continue;
                XYZ loc = GeometryHelper.GetFixtureLocation(fi);
                DaliPoint? here = loc != null ? new DaliPoint(loc.X, loc.Y) : (DaliPoint?)null;
                entries.Add(new DaliUnitEntry(
                    new DaliUnitReading("UID:" + fi.UniqueId, "", DaliUnitKind.Downlight, 0, ReadZone(fi), here),
                    fi.Id));
            }

            return new DaliUnitScan(entries, warnings);
        }

        /// <summary>Convenience projection for callers that only need the Revit-free readings.</summary>
        public static List<DaliUnitReading> Enumerate(Document doc) =>
            Scan(doc).Entries.Select(e => e.Reading).ToList();

        private static void AddDriverUnits(
            List<DaliUnitEntry> entries, List<string> warnings, List<FamilyInstance> drivers,
            string circuitKey, string circuitZone, DaliPoint? centroid)
        {
            // Parse each driver's ordinal off its Switch ID suffix; the base is ignored (non-unique placeholder).
            var parsed = drivers
                .Select(d => (elem: d, ord: DaliDriverOrdinal.FromSwitchId(ParameterHelper.GetSwitchID(d))))
                .ToList();

            var dupOrdinals = parsed.GroupBy(p => p.ord).Where(g => g.Count() > 1)
                                    .Select(g => g.Key).ToHashSet();

            foreach (var (elem, ord) in parsed)
            {
                // A clean ordinal keys as circuit#ordinal (redeploy-stable). A collided one is disambiguated by
                // ElementId so no address is dropped — at the cost of redeploy stability, which the warning flags.
                string unitKey = dupOrdinals.Contains(ord)
                    ? circuitKey + "#" + ord + "@" + elem.Id.ToRef().Value
                    : circuitKey + "#" + ord;

                entries.Add(new DaliUnitEntry(
                    new DaliUnitReading(unitKey, circuitKey, DaliUnitKind.Driver, ord, circuitZone, centroid),
                    elem.Id));
            }

            if (dupOrdinals.Count > 0)
            {
                string where = circuitZone.Length > 0 ? $"Zone \"{circuitZone}\"" : "An unzoned circuit";
                warnings.Add(
                    $"{where}: {drivers.Count} drivers on one circuit share a Switch ID suffix — their DALI "
                    + "addresses were assigned but won't survive a driver redeploy. Give each driver a distinct "
                    + "Switch ID (run TurboNumber), then re-address.");
            }
        }

        /// <summary>The set of Lighting Device <c>FamilySymbol</c> ids that are valid drivers — the single
        /// definition (<see cref="FamilyTypeCollectorService"/>), so DALI's notion of a driver matches
        /// TurboDriver/TurboRPS exactly (and excludes DMX decoders / keypads / sensors).</summary>
        private static HashSet<ElementId> GetValidDriverSymbolIds(Document doc)
        {
            var typeService = new FamilyTypeCollectorService();
            var symbols = typeService.GetAllLightingDeviceTypes(doc);
            return typeService.GetDriverCandidates(symbols)
                .Where(c => c.IsValidDriver)
                .Select(c => c.SymbolRef.ToElementId())
                .ToHashSet();
        }

        private static bool IsDali(FamilyInstance fi)
            => ParameterHelper.GetDimmingProtocol(fi).Trim()
                .Equals(DaliProtocol, System.StringComparison.OrdinalIgnoreCase);

        private static string ReadZone(FamilyInstance fi)
            => fi.LookupParameter(ParameterNames.ControlZone)?.AsString()?.Trim() ?? "";
    }
}
