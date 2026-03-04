#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Driver.Models;
using TurboSuite.Driver.Services;

namespace TurboSuite.Driver.ViewModels
{
    /// <summary>
    /// Main ViewModel orchestrating the entire window
    /// </summary>
    public class MainViewModel : ViewModelBase
    {
        private readonly Document _doc;
        private readonly UIDocument _uidoc;
        private readonly ElementUpdateService _updateService;
        private readonly List<DriverCandidateInfo> _driverCandidates;

        public ObservableCollection<CircuitViewModel> Circuits { get; set; }
        public List<FamilySymbol> AvailableLightingDeviceTypes { get; set; }

        public int TotalPowerSupplies => Circuits
            .SelectMany(c => c.DeviceGroups)
            .SelectMany(g => g.Devices)
            .Count();

        public MainViewModel(Document doc, UIDocument uidoc, List<CircuitData> circuitData,
            List<FamilySymbol> availableTypes, List<DriverCandidateInfo> driverCandidates)
        {
            _doc = doc;
            _uidoc = uidoc;
            _updateService = new ElementUpdateService(doc, uidoc);
            _driverCandidates = driverCandidates;

            // Filter available types to only valid drivers for combo boxes
            var validDriverTypeIds = new HashSet<ElementId>(
                driverCandidates.Where(c => c.IsValidDriver).Select(c => c.FamilySymbol.Id));
            AvailableLightingDeviceTypes = availableTypes
                .Where(t => validDriverTypeIds.Contains(t.Id))
                .ToList();

            Circuits = new ObservableCollection<CircuitViewModel>();

            LoadData(circuitData, validDriverTypeIds);
        }

        private void LoadData(List<CircuitData> circuitData, HashSet<ElementId> validDriverTypeIds)
        {
            foreach (var data in circuitData.OrderBy(c => c.CircuitNumber))
            {
                CircuitViewModel circuitVM = new CircuitViewModel(data, _driverCandidates);

                // Collect circuit fixture dimming protocols
                var circuitDimmingProtocols = new HashSet<string>(
                    data.LightingFixtures
                        .Where(f => !string.IsNullOrWhiteSpace(f.DimmingProtocol))
                        .Select(f => f.DimmingProtocol),
                    StringComparer.OrdinalIgnoreCase);

                // Collect circuit fixture voltages
                var circuitVoltages = new HashSet<string>(
                    data.LightingFixtures
                        .Where(f => !string.IsNullOrWhiteSpace(f.Voltage))
                        .Select(f => f.Voltage),
                    StringComparer.OrdinalIgnoreCase);

                // Get the recommended candidate from the circuit's recommendation
                DriverCandidateInfo recommendedCandidate = circuitVM.DriverRecommendation?.RecommendedCandidate;
                bool hasMatch = circuitVM.DriverRecommendation?.HasMatch ?? false;

                foreach (var kvp in data.DevicesByType.OrderBy(x => x.Key))
                {
                    // Filter out non-driver devices: skip device groups whose type is not a valid driver
                    var devicesInGroup = kvp.Value;
                    bool groupHasValidDriverType = devicesInGroup.Any(d => validDriverTypeIds.Contains(d.CurrentFamilyTypeId));
                    if (!groupHasValidDriverType)
                        continue;

                    DeviceGroupViewModel groupVM = new DeviceGroupViewModel(kvp.Key);

                    foreach (var deviceData in devicesInGroup.OrderBy(d => d.SwitchID))
                    {
                        LightingDeviceViewModel deviceVM = new LightingDeviceViewModel(
                            deviceData,
                            recommendedCandidate,
                            circuitDimmingProtocols,
                            circuitVoltages,
                            _driverCandidates,
                            hasMatch);

                        deviceVM.FamilyTypeChanged += (sender, args) => OnDeviceFamilyTypeChanged(deviceVM, circuitVM);

                        groupVM.Devices.Add(deviceVM);
                    }

                    circuitVM.DeviceGroups.Add(groupVM);
                }

                Circuits.Add(circuitVM);
            }
        }

        private void OnDeviceFamilyTypeChanged(LightingDeviceViewModel deviceVM, CircuitViewModel circuitVM)
        {
            if (deviceVM.SelectedFamilyType == null)
                return;

            if (deviceVM.SelectedFamilyType.Id == deviceVM.Data.CurrentFamilyTypeId)
                return;

            try
            {
                bool success = _updateService.ChangeFamilyType(
                    deviceVM.Data.DeviceId,
                    deviceVM.SelectedFamilyType);

                if (success)
                {
                    string oldTypeName = deviceVM.Data.CurrentFamilyTypeName;
                    string newTypeName = deviceVM.SelectedFamilyType.Name;

                    deviceVM.UpdateDeviceData(deviceVM.SelectedFamilyType);

                    if (oldTypeName != newTypeName)
                    {
                        RegroupDevices(circuitVM);
                    }
                }
                else
                {
                    MessageBox.Show(
                        "Failed to update device type. Please try again.",
                        "Update Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    deviceVM.SelectedFamilyType = deviceVM.AvailableFamilyTypes
                        .FirstOrDefault(t => t.Id == deviceVM.Data.CurrentFamilyTypeId);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error updating device:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                deviceVM.SelectedFamilyType = deviceVM.AvailableFamilyTypes
                    .FirstOrDefault(t => t.Id == deviceVM.Data.CurrentFamilyTypeId);
            }
        }

        private void RegroupDevices(CircuitViewModel circuitVM)
        {
            var allDeviceVMs = new List<LightingDeviceViewModel>();
            foreach (var group in circuitVM.DeviceGroups.ToList())
            {
                foreach (var deviceVM in group.Devices)
                {
                    allDeviceVMs.Add(deviceVM);
                }
            }

            circuitVM.DeviceGroups.Clear();
            circuitVM.Data.DevicesByType.Clear();

            var groupedDevices = allDeviceVMs
                .GroupBy(dvm => dvm.Data.CurrentFamilyTypeName)
                .OrderBy(g => g.Key);

            foreach (var group in groupedDevices)
            {
                DeviceGroupViewModel groupVM = new DeviceGroupViewModel(group.Key);

                circuitVM.Data.DevicesByType[group.Key] = new List<DeviceData>();

                foreach (var deviceVM in group.OrderBy(d => d.SwitchID))
                {
                    groupVM.Devices.Add(deviceVM);
                    circuitVM.Data.DevicesByType[group.Key].Add(deviceVM.Data);
                }

                circuitVM.DeviceGroups.Add(groupVM);
            }

            OnPropertyChanged(nameof(TotalPowerSupplies));
        }
    }
}
