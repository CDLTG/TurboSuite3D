#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using TurboSuite.Driver.Models;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Driver.ViewModels
{
    /// <summary>
    /// ViewModel for individual lighting device with editable family type
    /// </summary>
    public class LightingDeviceViewModel : ViewModelBase
    {
        private readonly DeviceData _data;
        private FamilySymbol _selectedFamilyType;

        public DeviceData Data => _data;

        public string SwitchID => string.IsNullOrWhiteSpace(_data.SwitchID) ? "? " : _data.SwitchID;

        public List<FamilySymbol> AvailableFamilyTypes { get; set; }

        public ElementId RecommendedFamilyTypeId { get; }

        public FamilySymbol SelectedFamilyType
        {
            get => _selectedFamilyType;
            set
            {
                if (SetProperty(ref _selectedFamilyType, value))
                {
                    FamilyTypeChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public event EventHandler FamilyTypeChanged;

        public LightingDeviceViewModel(
            DeviceData data,
            DriverCandidateInfo recommendedCandidate,
            HashSet<string> circuitDimmingProtocols,
            HashSet<string> circuitVoltages,
            List<DriverCandidateInfo> allCandidates,
            bool hasMatch)
        {
            _data = data;
            RecommendedFamilyTypeId = recommendedCandidate?.FamilySymbol?.Id ?? ElementId.InvalidElementId;
            AvailableFamilyTypes = BuildTypeList(circuitDimmingProtocols, circuitVoltages, allCandidates, hasMatch);

            _selectedFamilyType = AvailableFamilyTypes.Find(t => t.Id == data.CurrentFamilyTypeId);
        }

        private List<FamilySymbol> BuildTypeList(
            HashSet<string> circuitDimmingProtocols,
            HashSet<string> circuitVoltages,
            List<DriverCandidateInfo> allCandidates,
            bool hasMatch)
        {
            // No match: show all lighting device types alphabetically, no highlight
            if (!hasMatch)
            {
                return allCandidates
                    .OrderBy(c => c.FamilyTypeName)
                    .Select(c => c.FamilySymbol)
                    .ToList();
            }

            // Only include valid driver types
            var validCandidates = allCandidates.Where(c => c.IsValidDriver).ToList();

            // Filter by dimming protocol: exclude candidates whose protocol is set but doesn't match
            if (circuitDimmingProtocols.Count > 0)
            {
                validCandidates = validCandidates
                    .Where(c => string.IsNullOrWhiteSpace(c.DimmingProtocol)
                                || circuitDimmingProtocols.Contains(c.DimmingProtocol))
                    .ToList();
            }

            // Filter by voltage: exclude candidates whose voltage is set but doesn't match
            if (circuitVoltages.Count > 0)
            {
                validCandidates = validCandidates
                    .Where(c => string.IsNullOrWhiteSpace(c.Voltage)
                                || circuitVoltages.Contains(c.Voltage))
                    .ToList();
            }

            return validCandidates
                .OrderBy(c => c.FamilyTypeName)
                .Select(c => c.FamilySymbol)
                .ToList();
        }

        /// <summary>
        /// Update the underlying data after a successful type change
        /// </summary>
        public void UpdateDeviceData(FamilySymbol newType)
        {
            _data.CurrentFamilyTypeId = newType.Id;
            _data.CurrentFamilyTypeName = newType.Name;
        }
    }
}
