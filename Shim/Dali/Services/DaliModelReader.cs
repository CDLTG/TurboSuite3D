#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Dali.Addressing;
using TurboSuite.Dali.Services;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Dali.Services
{
    /// <summary>
    /// Shim-side <see cref="IDaliModelReader"/> — the addressing read. Walks the model's electrical
    /// circuits and, for every circuit carrying DALI fixtures, produces a <see cref="DaliCircuitReading"/>:
    /// the circuit's <c>UniqueId</c> (the load anchor), its Control Zone (first non-blank among its DALI
    /// fixtures), and the <b>centroid of its lighting fixtures</b> (the remote driver/decoder device is a
    /// lighting <i>device</i>, deliberately excluded from the centroid so its arbitrary ceiling spot can't
    /// drag the spatial order).
    ///
    /// This is the identity-preserving sibling of <c>DaliDemandProvider.CountDaliLoadsByZone</c>: the demand
    /// side needs only counts (and throws identity away); addressing needs the per-circuit key + geometry.
    /// Both walk <c>circuit.Elements</c> the same way and both scope DALI membership on the fixture's
    /// <c>Dimming Protocol = DALI</c>.
    /// </summary>
    public sealed class DaliModelReader : IDaliModelReader
    {
        private const string DaliProtocol = "DALI";
        private readonly Document _doc;

        public DaliModelReader(Document doc) => _doc = doc;

        public DaliModelSnapshot Read()
        {
            var lightingCatId = new ElementId(BuiltInCategory.OST_LightingFixtures);
            var readings = new List<DaliCircuitReading>();
            var circuited = new HashSet<ElementId>();

            var circuits = new FilteredElementCollector(_doc)
                .OfClass(typeof(ElectricalSystem))
                .OfCategory(BuiltInCategory.OST_ElectricalCircuit)
                .Cast<ElectricalSystem>();

            foreach (var circuit in circuits)
            {
                var pts = new List<XYZ>();
                string zone = "";
                bool hasDali = false;

                foreach (Element el in circuit.Elements)
                {
                    if (!(el is FamilyInstance fi) || fi.Category?.Id != lightingCatId || !IsDali(fi)) continue;

                    hasDali = true;
                    circuited.Add(fi.Id);

                    XYZ loc = GeometryHelper.GetFixtureLocation(fi);
                    if (loc != null) pts.Add(loc);

                    if (zone.Length == 0)
                    {
                        string z = ReadZone(fi);
                        if (z.Length > 0) zone = z;
                    }
                }

                if (!hasDali) continue;

                DaliPoint? centroid = pts.Count > 0
                    ? new DaliPoint(pts.Average(p => p.X), pts.Average(p => p.Y))
                    : (DaliPoint?)null;

                readings.Add(new DaliCircuitReading(circuit.UniqueId, zone, centroid));
            }

            // Uncircuited DALI fixtures — warned-and-excluded, counted for the "circuit them" nudge.
            int uncircuited = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_LightingFixtures)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Count(fi => !circuited.Contains(fi.Id) && IsDali(fi));

            return new DaliModelSnapshot(readings, uncircuited);
        }

        private static bool IsDali(FamilyInstance fi)
            => ParameterHelper.GetDimmingProtocol(fi).Trim()
                .Equals(DaliProtocol, StringComparison.OrdinalIgnoreCase);

        private static string ReadZone(FamilyInstance fi)
            => fi.LookupParameter(ParameterNames.ControlZone)?.AsString()?.Trim() ?? "";
    }
}
