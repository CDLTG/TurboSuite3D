#nullable disable
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Dmx;
using TurboSuite.Dmx.Input;
using TurboSuite.Dmx.Services;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Dmx.Services
{
    /// <summary>
    /// Reads the active document into a <see cref="DmxModelSnapshot"/> for TurboDMX — the DMX fixtures
    /// (with their <c>Control Zone</c> + <c>DMX Channels</c> + length/power) and the discovered
    /// decoder/driver candidate pools. READ-ONLY: no transaction, no writes.
    ///
    /// Keys on the confirmed schema in <see cref="DmxParameterNames"/>. Defensive throughout: an
    /// element/symbol missing a required param is simply skipped, never an exception.
    /// </summary>
    public sealed class DmxModelReader : IDmxModelReader
    {
        private readonly Document _doc;

        public DmxModelReader(Document doc) => _doc = doc;

        public DmxModelSnapshot Read() => new DmxModelSnapshot
        {
            Fixtures = ReadFixtures(),
            DecoderCandidates = ReadDecoderCandidates(),
            DriverCandidates = ReadDriverCandidates(),
        };

        // ── DMX fixtures (OST_LightingFixtures with a DMX Channels value) ──────────────────────────
        private List<DmxFixtureReading> ReadFixtures()
        {
            var result = new List<DmxFixtureReading>();

            var fixtures = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_LightingFixtures)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>();

            foreach (var fi in fixtures)
            {
                // Subsystem membership routes on the authored Dimming Protocol, NOT the channel count.
                // (Was DMX Channels > 0 — that overloaded a count as a membership flag and disagreed with
                // TurboZones' protocol-based routing on a mis-authored fixture, silently dropping its
                // circuit. Now DMX Channels is read for the math only; a DMX fixture with 0 channels flows
                // through to be reported as a zero-channel error by DmxZoneBuilder.)
                string protocol = ReadStringIT(fi, ParameterNames.DimmingProtocol).Trim();
                if (!protocol.Equals("DMX", System.StringComparison.OrdinalIgnoreCase)) continue;

                // DMX Channels is typically a TYPE param; Control Zone is instance. Read both with an
                // instance→type fallback so the reader works however the shared params are bound.
                int channels = ReadIntIT(fi, DmxParameterNames.DmxChannels);

                // The engine only needs total watts + channels per fixture; length is just how watts are
                // expressed (watts = LengthFt × WattsPerFt). Linear fixtures carry a real length and a
                // pre-calculated total wattage (Linear Power); point fixtures (downlights/sheets) have
                // Linear Length = 0, so we carry their watts as a unit-length run (length 1 × watts).
                double linearLength = ParameterHelper.GetLinearLength(fi);   // feet (0 for point fixtures)
                double totalWatts = TotalWatts(fi);

                double lengthFt, wattsPerFt;
                if (linearLength > 0.0001)
                {
                    lengthFt = linearLength;
                    wattsPerFt = totalWatts / linearLength;
                }
                else
                {
                    lengthFt = 1.0;          // unit length so length × W/ft = total watts
                    wattsPerFt = totalWatts;
                }

                // Bundle Size (max fixtures per daisy-chain) is a TYPE trait; read instance→type, clamp
                // ≤0 ⇒ 1 so a missing/older family means "no bundling" (each fixture packs on its own).
                int bundleSize = ReadIntIT(fi, DmxParameterNames.BundleSize);
                if (bundleSize < 1) bundleSize = 1;

                result.Add(new DmxFixtureReading
                {
                    ElementId = fi.Id.ToRef().Value,
                    ControlZone = ReadStringIT(fi, DmxParameterNames.ControlZone),
                    Channels = channels,
                    LengthFt = lengthFt,
                    WattsPerFt = wattsPerFt,
                    MaxPerBundle = bundleSize,
                    TypeMark = BundleKey(fi),
                });
            }

            return result;
        }

        // ── Decoder candidates: device family types carrying the decoder cap params ──────────────────
        private List<DmxDecoderCandidate> ReadDecoderCandidates()
        {
            return DeviceSymbols()
                .Where(s => ReadInt(s, DmxParameterNames.DmxChannels) > 0)
                .Select(s => new DmxDecoderCandidate
                {
                    TypeId = s.UniqueId,
                    Name = SymbolName(s),
                    MaxOutputs = ReadInt(s, DmxParameterNames.DmxChannels),
                    MaxAmpsPerOutput = ReadAmps(s, DmxParameterNames.DecoderAmpsPerChannel),
                    MaxWatts = ReadWatts(s, DmxParameterNames.Power),   // shared Power param = C2 cap
                })
                .ToList();
        }

        // ── Driver candidates: device family types that are NOT decoders but carry a voltage ─────────
        private List<DmxDriverCandidate> ReadDriverCandidates()
        {
            return DeviceSymbols()
                .Where(s => ReadInt(s, DmxParameterNames.DmxChannels) <= 0)
                .Select(s => new
                {
                    Symbol = s,
                    Volts = ParseDouble(ParameterHelper.GetVoltage(s)),
                })
                .Where(x => x.Volts > 0)
                .Select(x => new DmxDriverCandidate
                {
                    TypeId = x.Symbol.UniqueId,
                    Name = SymbolName(x.Symbol),
                    TypeMark = StringOf(x.Symbol.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)) ?? "",
                    RatedWatts = ParameterHelper.GetDriverPower(x.Symbol),
                    OperatingVolts = x.Volts,
                    DeratingFactorRaw = ParameterHelper.GetDeratingFactor(x.Symbol),
                })
                .ToList();
        }

        private IEnumerable<FamilySymbol> DeviceSymbols() =>
            new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_LightingDevices)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>();

        // Total connected watts. Linear fixtures pre-calculate Linear Power (= Linear Length ×
        // Power Per Length); point fixtures (Linear Power = 0) fall back to the Power param (instance→type).
        private static double TotalWatts(FamilyInstance fi)
        {
            double linearPower = ParameterHelper.GetLinearPower(fi);
            if (linearPower > 0.0001) return linearPower;
            return WattsOf(ResolveIT(fi, DmxParameterNames.Power));
        }

        // The bundler's "same product" key: a fixture's Type Mark. Two fixtures chain into one bundle
        // only when this matches. A blank Type Mark can't identify a product, so fall back to a per-type
        // token (the symbol UniqueId) — unmarked fixtures of different types then never pool together.
        private static string BundleKey(FamilyInstance fi)
        {
            string mark = StringOf(fi.Symbol?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK));
            return string.IsNullOrWhiteSpace(mark) ? "type:" + (fi.Symbol?.UniqueId ?? "") : mark;
        }

        // Label row = the Catalog Number1 parameter (not the family name), then the type name.
        // Falls back to the family name if it's unset, so a row never renders as " : Type".
        private static string SymbolName(FamilySymbol s)
        {
            string catalog = StringOf(s.LookupParameter(ParameterNames.CatalogNumber1));
            if (string.IsNullOrWhiteSpace(catalog)) catalog = s.FamilyName;
            return $"{catalog} : {s.Name}";
        }

        // ── Element readers (used for decoder/driver TYPE symbols — read the symbol directly) ─────────
        private static int ReadInt(Element e, string name) => IntOf(e?.LookupParameter(name));
        private static double ReadWatts(Element e, string name) => WattsOf(e?.LookupParameter(name));
        private static double ReadAmps(Element e, string name) => AmpsOf(e?.LookupParameter(name));

        // ── Fixture readers: instance param, falling back to the type (covers either binding) ─────────
        private static int ReadIntIT(FamilyInstance fi, string name) => IntOf(ResolveIT(fi, name));
        private static string ReadStringIT(FamilyInstance fi, string name) => StringOf(ResolveIT(fi, name));

        private static Parameter ResolveIT(FamilyInstance fi, string name)
        {
            var p = fi?.LookupParameter(name);
            if (p != null && p.HasValue) return p;
            return fi?.Symbol?.LookupParameter(name);
        }

        // ── Parameter cores ──────────────────────────────────────────────────────────────────────────
        private static int IntOf(Parameter p)
        {
            if (p == null || !p.HasValue) return 0;
            return p.StorageType == StorageType.Integer ? p.AsInteger()
                 : p.StorageType == StorageType.Double ? (int)System.Math.Round(p.AsDouble())
                 : 0;
        }

        private static double WattsOf(Parameter p)
        {
            if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return 0.0;
            try { return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Watts); }
            catch { return p.AsDouble(); }
        }

        private static double AmpsOf(Parameter p)
        {
            if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return 0.0;
            try { return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Amperes); }
            catch { return p.AsDouble(); }
        }

        private static string StringOf(Parameter p) =>
            p != null && p.HasValue && p.StorageType == StorageType.String ? (p.AsString() ?? "") : "";

        private static double ParseDouble(string s) =>
            double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0.0;
    }
}
