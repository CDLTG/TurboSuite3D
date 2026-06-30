#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Dmx;
using TurboSuite.Driver.Models;
using TurboSuite.Shared.Constants;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Service for collecting family types
    /// </summary>
    public class FamilyTypeCollectorService
    {
        // A driver type is the TBD placeholder if its FAMILY name (never the type name)
        // contains this token. Shipped as the "AL_RPS_TBD" family in the Revit template.
        private const string TbdFamilyMarker = "TBD";

        /// <summary>
        /// Get all Lighting Device family types in the project
        /// </summary>
        public List<FamilySymbol> GetAllLightingDeviceTypes(Document doc)
        {
            FilteredElementCollector collector = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_LightingDevices);

            List<FamilySymbol> types = collector
                .Cast<FamilySymbol>()
                .OrderBy(fs => fs.FamilyName)
                .ThenBy(fs => fs.Name)
                .ToList();

            return types;
        }

        /// <summary>
        /// Build DriverCandidateInfo for each FamilySymbol, reading Power, Sub-Driver Power, and Manufacturer
        /// </summary>
        public List<DriverCandidateInfo> GetDriverCandidates(List<FamilySymbol> allTypes)
        {
            var candidates = new List<DriverCandidateInfo>();

            foreach (var symbol in allTypes)
            {
                double power = ParameterHelper.GetDriverPower(symbol);
                double subPower = ParameterHelper.GetSubDriverPower(symbol);
                string manufacturer = ParameterHelper.GetManufacturer(symbol);
                string dimmingProtocol = ParameterHelper.GetDimmingProtocol(symbol);
                int maximumFixtures = ParameterHelper.GetMaximumFixtures(symbol);
                string voltage = ParameterHelper.GetVoltage(symbol);
                double derateFactor = ParameterHelper.GetDeratingFactor(symbol);
                string catalogNumber = symbol.LookupParameter(ParameterNames.CatalogNumber1)?.AsString() ?? "";
                bool isTbd = symbol.FamilyName != null
                    && symbol.FamilyName.IndexOf(TbdFamilyMarker, StringComparison.OrdinalIgnoreCase) >= 0;

                // A DMX decoder (DMX Channels > 0) is a parallel class of power supply, NOT a
                // wattage-sized driver — same rule TurboDMX's model reader uses to split decoders from
                // drivers. Decoder families often also carry Power/Sub-Driver Power, which would
                // otherwise make them masquerade as valid drivers: counted as placed supplies and packed
                // into a wattage recommendation (the bogus "4 decoders → 2 drivers" repack). Exclude them
                // from the driver pool so TurboRPS leaves DMX sizing to TurboDMX. See TurboRPS-2.
                bool isDecoder = ReadDmxChannels(symbol) > 0;

                bool isValid = false;
                int subCount = 0;

                if (!isDecoder && power > 0 && subPower > 0)
                {
                    double remainder = power % subPower;
                    if (Math.Abs(remainder) < 0.01)
                    {
                        subCount = (int)Math.Round(power / subPower);
                        isValid = subCount > 0;
                    }
                }

                candidates.Add(new DriverCandidateInfo
                {
                    SymbolRef = symbol.Id.ToRef(),
                    CatalogNumber = catalogNumber,
                    FamilyTypeName = symbol.Name,
                    FamilyName = symbol.FamilyName,
                    Manufacturer = manufacturer,
                    TotalPower = power,
                    SubDriverPower = subPower,
                    SubDriverCount = subCount,
                    IsValidDriver = isValid,
                    DimmingProtocol = dimmingProtocol,
                    MaximumFixtures = maximumFixtures,
                    Voltage = voltage,
                    DerateFactor = derateFactor,
                    IsTbd = isTbd
                });
            }

            return candidates;
        }

        /// <summary>Read the integer "DMX Channels" value off a device type. &gt; 0 marks it a DMX
        /// decoder (matching the TurboDMX model reader). Returns 0 when absent.</summary>
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
