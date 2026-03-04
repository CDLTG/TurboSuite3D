#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Driver.Models;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Driver.Services
{
    /// <summary>
    /// Service for collecting family types
    /// </summary>
    public class FamilyTypeCollectorService
    {
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

                bool isValid = false;
                int subCount = 0;

                if (power > 0 && subPower > 0)
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
                    FamilySymbol = symbol,
                    FamilyTypeName = symbol.Name,
                    Manufacturer = manufacturer,
                    TotalPower = power,
                    SubDriverPower = subPower,
                    SubDriverCount = subCount,
                    IsValidDriver = isValid,
                    DimmingProtocol = dimmingProtocol,
                    MaximumFixtures = maximumFixtures,
                    Voltage = voltage
                });
            }

            return candidates;
        }
    }
}
