#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Abstractions;
using TurboSuite.Dmx;
using TurboSuite.Driver.Models;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Shim implementation of <see cref="IDriverMatchOperations"/> for the TurboDriver match-check
    /// window. Collects two type lists from the project, read-only, using the same ParameterHelper
    /// getters the real driver engine reads:
    ///
    /// <list type="bullet">
    /// <item><b>Drivers</b> — Lighting Devices that are driver-shaped: no <c>TurboSuite Role</c> (drivers
    /// take the default path) or the <c>DriverPlaceholder</c> (TBD), and NOT a DMX decoder
    /// (<c>DMX Channels &gt; 0</c>). This filters out keypads, switches, receptacles, shade motors,
    /// hybrid repeaters (all carry an explicit non-driver role) and decoders. An incomplete real
    /// driver stays in the list (it's roleless) so the grid can show <i>why</i> it isn't valid.</item>
    /// <item><b>RPS fixtures</b> — Lighting Fixtures with the <c>Remote Power Supply</c> type parameter
    /// checked (the only fixtures TurboDriver sizes a driver for).</item>
    /// </list>
    /// </summary>
    public class DriverMatchOperations : IDriverMatchOperations
    {
        private readonly UIDocument _uidoc;

        public DriverMatchOperations(UIDocument uidoc)
        {
            _uidoc = uidoc;
        }

        public MatchCatalog Collect() => Build(_uidoc?.Document);

        public static MatchCatalog Build(Document doc)
        {
            var catalog = new MatchCatalog();
            if (doc == null) return catalog;

            catalog.Drivers = CollectDrivers(doc);
            catalog.Fixtures = CollectFixtures(doc);
            return catalog;
        }

        private static List<DriverOption> CollectDrivers(Document doc)
        {
            var collector = new FamilyTypeCollectorService();

            // Filter to driver-shaped devices FIRST, then build candidates — candidate order mirrors
            // the input list, so index i pairs back to driverSymbols[i] for the display name.
            var driverSymbols = collector.GetAllLightingDeviceTypes(doc)
                .Where(IsDriverShaped)
                .ToList();

            var candidates = collector.GetDriverCandidates(driverSymbols);

            var options = new List<DriverOption>();
            for (int i = 0; i < candidates.Count && i < driverSymbols.Count; i++)
            {
                var sym = driverSymbols[i];
                options.Add(new DriverOption
                {
                    DisplayName = $"{sym.FamilyName} : {sym.Name}",
                    Driver = candidates[i]
                });
            }

            return options
                .OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<FixtureOption> CollectFixtures(Document doc)
        {
            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_LightingFixtures)
                .Cast<FamilySymbol>()
                .Where(ParameterHelper.HasRemotePowerSupply)
                .OrderBy(s => s.FamilyName)
                .ThenBy(s => s.Name)
                .ToList();

            var options = new List<FixtureOption>();
            foreach (var s in symbols)
            {
                string typeMark = s.LookupParameter(ParameterNames.TypeMark)?.AsString();
                var fixture = new FixtureData
                {
                    FixtureId = ElementRef.None,
                    TypeMark = typeMark,
                    // Type-level wattage only; a linear fixture's real wattage is per-instance
                    // (Linear Power) and isn't knowable here — the diagnostic treats wattage as info.
                    TypePower = ParameterHelper.GetDriverPower(s),
                    LinearPower = 0,
                    Manufacturer = ParameterHelper.GetManufacturer(s),
                    DimmingProtocol = ParameterHelper.GetDimmingProtocol(s),
                    Voltage = ParameterHelper.GetVoltage(s),
                    HasRemotePowerSupply = true
                };

                options.Add(new FixtureOption
                {
                    DisplayName = $"{s.FamilyName} : {s.Name}",
                    Fixture = fixture
                });
            }

            return options;
        }

        /// <summary>Roleless (or the TBD placeholder) and not a DMX decoder — see class summary.</summary>
        private static bool IsDriverShaped(FamilySymbol symbol)
        {
            string role = ParameterHelper.GetRole(symbol);
            bool isDriverRole = string.IsNullOrEmpty(role) || role == Roles.DriverPlaceholder;
            if (!isDriverRole) return false;
            return ReadDmxChannels(symbol) == 0;
        }

        /// <summary>Read the integer "DMX Channels" off a device type; &gt; 0 marks it a decoder
        /// (mirrors <c>FamilyTypeCollectorService</c>). Returns 0 when absent.</summary>
        private static int ReadDmxChannels(FamilySymbol symbol)
        {
            var p = symbol?.LookupParameter(DmxParameterNames.DmxChannels);
            if (p == null || !p.HasValue)
                return 0;
            return p.StorageType switch
            {
                StorageType.Integer => p.AsInteger(),
                StorageType.Double => (int)Math.Round(p.AsDouble()),
                _ => 0
            };
        }
    }
}
