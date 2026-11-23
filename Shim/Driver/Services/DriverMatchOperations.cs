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
using TurboSuite.Shared.Services;
using TurboSuite.Shared.Storage;

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
    /// <item><b>Links</b> — the project's fixture-type → driver-family links
    /// (<see cref="DriverLinkStorageService"/>), broken ones included.</item>
    /// </list>
    ///
    /// <para>Drivers are grouped into <b>families</b>: a link targets a family (TurboDriver still sizes
    /// among its types), so the tab picks a family and shows every type in it.</para>
    ///
    /// <para><b>Worksharing:</b> a link write borrows the link store (when it exists) and nothing else —
    /// same per-operation lease as <see cref="RpsRevitOperations"/>.</para>
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

            catalog.Families = CollectFamilies(doc);
            catalog.Fixtures = CollectFixtures(doc);
            catalog.Links = DriverLinkStorageService.LoadAll(doc);
            return catalog;
        }

        public bool? SetLink(ElementRef fixtureTypeRef, ElementRef driverFamilyRef)
        {
            var doc = _uidoc?.Document;
            if (doc == null) return false;

            if (doc.GetElement(fixtureTypeRef.ToElementId()) is not FamilySymbol fixtureType
                || doc.GetElement(driverFamilyRef.ToElementId()) is not Family family)
            {
                TaskDialog.Show("TurboRPS", "That fixture type or driver family no longer exists. Click Reload.");
                return false;
            }

            var entries = DriverLinkStorageService.LoadRaw(doc)
                .Where(e => !string.Equals(e.FixtureTypeKey, fixtureType.UniqueId, StringComparison.Ordinal))
                .ToList();
            entries.Add((fixtureType.UniqueId, family.UniqueId));

            return Write(doc, entries);
        }

        public bool? RemoveLink(string fixtureTypeKey)
        {
            var doc = _uidoc?.Document;
            if (doc == null || string.IsNullOrEmpty(fixtureTypeKey)) return false;

            var raw = DriverLinkStorageService.LoadRaw(doc);
            var entries = raw
                .Where(e => !string.Equals(e.FixtureTypeKey, fixtureTypeKey, StringComparison.Ordinal))
                .ToList();
            if (entries.Count == raw.Count) return true;   // already gone (e.g. a teammate removed it)

            return Write(doc, entries);
        }

        /// <summary>Lease the store (if it exists yet), then save. Null = refused or rolled back,
        /// already reported to the user.</summary>
        private static bool? Write(Document doc, List<(string FixtureTypeKey, string DriverFamilyKey)> entries)
        {
            var storeId = DriverLinkStorageService.FindStorageId(doc);
            var workingSet = storeId != null ? new[] { storeId } : Array.Empty<ElementId>();
            var lease = WorksharingLease.Acquire(doc, workingSet, "driver link");
            if (!lease.IsGranted)
            {
                TaskDialog.Show("TurboRPS", lease.DenialMessage);
                return null;
            }

            var outcome = DriverLinkStorageService.Save(doc, entries);
            if (outcome == StorageSaveOutcome.RolledBack) TaskDialog.Show("TurboRPS", WorksharingMessages.RolledBack);
            return outcome == StorageSaveOutcome.Saved ? true : null;
        }

        private static List<DriverFamilyOption> CollectFamilies(Document doc)
        {
            var collector = new FamilyTypeCollectorService();

            var driverSymbols = collector.GetAllLightingDeviceTypes(doc)
                .Where(IsDriverShaped)
                .ToList();

            var candidates = collector.GetDriverCandidates(driverSymbols);

            return candidates
                .GroupBy(c => c.FamilyRef)
                .Select(g => new DriverFamilyOption
                {
                    FamilyRef = g.Key,
                    FamilyName = g.First().FamilyName,
                    IsTbd = g.Any(c => c.IsTbd),
                    Types = g.OrderBy(c => c.TotalPower)
                             .ThenBy(c => c.FamilyTypeName, NaturalStringComparer.OrdinalIgnoreCase)
                             .ToList()
                })
                .OrderBy(f => f.FamilyName, StringComparer.OrdinalIgnoreCase)
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
                string display = $"{s.FamilyName} : {s.Name}";
                var fixture = new FixtureData
                {
                    FixtureId = ElementRef.None,
                    TypeRef = s.Id.ToRef(),
                    TypeDisplayName = display,
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
                    DisplayName = display,
                    TypeRef = s.Id.ToRef(),
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
