#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;
using TurboSuite.Abstractions;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Zones.ViewModels
{
    public class PanelBreakdownTabViewModel : ViewModelBase
    {
        private static string[] ModuleTypeOrder => PanelAllocationService.ModuleTypeOrder;

        private string _selectedBrandName;
        private bool _useDedicatedRelayModule;
        private PanelAllocationResult _allocationResult;
        private ObservableCollection<LocationDisplayViewModel> _locationDisplays;
        private ObservableCollection<BomLineItem> _bomItems;
        private readonly Dictionary<string, string> _specialDeviceSelections = new Dictionary<string, string>();
        private readonly Dictionary<string, int> _panelSizeOverrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly int _keypadCount;
        private readonly int _twoGangKeypadCount;
        private readonly int _hybridRepeaterCount;
        private readonly string _hybridRepeaterPartNumber;

        /// <summary>What the control subsystems (TurboDMX today) reported at window open. Read once —
        /// re-solving DMX on every panel-size tweak would be wasted work, and the DMX design cannot
        /// change while this window is the one in front of the user.</summary>
        private readonly IReadOnlyList<ControlSubsystemDemand> _subsystemDemands;
        private readonly IRevitWorkQueue _workQueue;
        private readonly IPanelSettingsStore _settingsStore;
        private BrandConfig _currentBrand;
        private ObservableCollection<ZonesCircuitData> _unassignedCircuits;

        // Coalesce concurrent SaveSettings raises so we never lose user changes to a dropped event.
        // _savePending = a SavePanelSettingsRequest is in flight on the Revit side.
        // _saveDirty   = additional changes arrived during that flight; re-raise on completion.
        private bool _savePending;
        private bool _saveDirty;

        public PanelBreakdownTabViewModel(List<ZonesCircuitData> circuits,
            int keypadCount, int twoGangKeypadCount,
            int hybridRepeaterCount, string hybridRepeaterPartNumber,
            PanelSettings savedSettings,
            IRevitWorkQueue workQueue, IPanelSettingsStore settingsStore,
            IReadOnlyList<ControlSubsystemDemand> subsystemDemands = null)
        {
            _workQueue = workQueue;
            _settingsStore = settingsStore;
            _subsystemDemands = subsystemDemands;
            _keypadCount = keypadCount;
            _twoGangKeypadCount = twoGangKeypadCount;
            _hybridRepeaterCount = hybridRepeaterCount;
            _hybridRepeaterPartNumber = hybridRepeaterPartNumber;

            Circuits = new ObservableCollection<ZonesCircuitViewModel>(
                circuits.OrderBy(c => c.CircuitNumber).Select(c => new ZonesCircuitViewModel(c)));

            BrandNames = new List<string> { "Lutron", "Crestron" };
            _selectedBrandName = "Lutron";

            ApplySavedSettings(savedSettings);
        }

        public string TabHeader => "Panel Breakdown";

        public ObservableCollection<ZonesCircuitViewModel> Circuits { get; }

        public List<string> BrandNames { get; }

        public string SelectedBrandName
        {
            get => _selectedBrandName;
            set
            {
                if (SetProperty(ref _selectedBrandName, value))
                {
                    OnPropertyChanged(nameof(IsLutronSelected));
                    // Clear panel size overrides when brand changes (sizes differ)
                    _panelSizeOverrides.Clear();
                    BuildPanelBreakdown();
                }
            }
        }

        public bool IsLutronSelected => string.Equals(_selectedBrandName, "Lutron", StringComparison.OrdinalIgnoreCase);

        public bool UseDedicatedRelayModule
        {
            get => _useDedicatedRelayModule;
            set
            {
                if (SetProperty(ref _useDedicatedRelayModule, value))
                    BuildPanelBreakdown();
            }
        }

        public PanelAllocationResult AllocationResult
        {
            get => _allocationResult;
            private set
            {
                if (SetProperty(ref _allocationResult, value))
                    OnPropertyChanged(nameof(ShowPlaceholder));
            }
        }

        public bool ShowPlaceholder => _allocationResult == null;

        public ObservableCollection<LocationDisplayViewModel> LocationDisplays
        {
            get => _locationDisplays;
            private set => SetProperty(ref _locationDisplays, value);
        }

        public ObservableCollection<BomLineItem> BomItems
        {
            get => _bomItems;
            private set => SetProperty(ref _bomItems, value);
        }

        private ObservableCollection<PanelResult> _processorDisplays;
        public ObservableCollection<PanelResult> ProcessorDisplays
        {
            get => _processorDisplays;
            private set
            {
                if (SetProperty(ref _processorDisplays, value))
                    OnPropertyChanged(nameof(ShowProcessorPlaceholder));
            }
        }

        public bool ShowProcessorPlaceholder => _processorDisplays == null || _processorDisplays.Count == 0;

        public ObservableCollection<ZonesCircuitData> UnassignedCircuits
        {
            get => _unassignedCircuits;
            private set
            {
                if (SetProperty(ref _unassignedCircuits, value))
                    OnPropertyChanged(nameof(HasUnassigned));
            }
        }

        public bool HasUnassigned => _unassignedCircuits != null && _unassignedCircuits.Count > 0;

        private void BuildPanelBreakdown()
        {
            // Save special device selections before rebuilding
            SaveSpecialDeviceSelections();

            // Detach old panel event handlers
            DetachPanelHandlers();

            _currentBrand = _selectedBrandName == "Crestron"
                ? BrandConfig.Crestron
                : BrandConfig.CreateLutron(_useDedicatedRelayModule);

            var circuitData = Circuits.Select(c => c.Data).ToList();

            var (result, unassigned) = PanelAllocationService.BuildPanelBreakdown(
                circuitData, _currentBrand, _panelSizeOverrides);

            AllocationResult = result;
            UnassignedCircuits = new ObservableCollection<ZonesCircuitData>(unassigned);

            // Restore special device selections (no auto-lock — processor is manual)
            RestoreSpecialDeviceSelections();
            AttachPanelHandlers();
            RebuildSubsystemDetails();
            RebuildLinkAssignments();

            // Build location displays for XAML binding
            var displays = new ObservableCollection<LocationDisplayViewModel>();
            for (int i = 0; i < AllocationResult.Locations.Count; i++)
            {
                displays.Add(new LocationDisplayViewModel
                {
                    Location = AllocationResult.Locations[i],
                    IsLastLocation = (i == AllocationResult.Locations.Count - 1)
                });
            }
            LocationDisplays = displays;

            RebuildBom();
            SaveSettings();
        }

        private void ApplySavedSettings(PanelSettings settings)
        {
            if (settings != null)
            {
                _selectedBrandName = settings.Brand ?? "Lutron";
                _useDedicatedRelayModule = settings.UseDedicatedRelayModule;
                OnPropertyChanged(nameof(SelectedBrandName));
                OnPropertyChanged(nameof(IsLutronSelected));
                OnPropertyChanged(nameof(UseDedicatedRelayModule));

                // Restore special device selections
                foreach (var kvp in settings.SpecialDeviceSelections)
                    _specialDeviceSelections[kvp.Key] = kvp.Value;

                // Restore panel size overrides
                foreach (var kvp in settings.PanelSizeOverrides)
                    _panelSizeOverrides[kvp.Key] = kvp.Value;
            }

            // Auto-build on load
            BuildPanelBreakdown();
        }

        private PanelSettings BuildSettingsSnapshot()
        {
            var settings = new PanelSettings
            {
                Brand = _selectedBrandName,
                UseDedicatedRelayModule = _useDedicatedRelayModule
            };

            // Save current special device selections
            if (_allocationResult != null)
            {
                foreach (var panel in _allocationResult.AllPanels)
                {
                    if (panel.HasSpecialCompartment
                        && !string.IsNullOrEmpty(panel.SelectedSpecialDevice)
                        && panel.SelectedSpecialDevice != "Empty")
                    {
                        settings.SpecialDeviceSelections[panel.PanelName] = panel.SelectedSpecialDevice;
                    }

                    if (panel.HasDualSpecialCompartment
                        && !string.IsNullOrEmpty(panel.SelectedSpecialDevice2)
                        && panel.SelectedSpecialDevice2 != "Empty")
                    {
                        settings.SpecialDeviceSelections[panel.PanelName + "#2"] = panel.SelectedSpecialDevice2;
                    }
                }
            }

            // Save panel size overrides
            foreach (var kvp in _panelSizeOverrides)
                settings.PanelSizeOverrides[kvp.Key] = kvp.Value;

            return settings;
        }

        private void SaveSettings()
        {
            // Coalesce: if a save is already in flight, mark dirty and let the completion
            // callback re-enqueue with a fresh snapshot. This guards against losing user
            // changes (e.g. rapid ComboBox toggles) while a save is on the Revit thread.
            if (_savePending)
            {
                _saveDirty = true;
                return;
            }

            RaiseSaveSettings();
        }

        private void RaiseSaveSettings()
        {
            _savePending = true;
            _saveDirty = false;

            var snapshot = BuildSettingsSnapshot();
            _workQueue.Enqueue(
                () => { _settingsStore.Save(snapshot); return null; },
                _ =>
                {
                    if (_saveDirty)
                    {
                        // User changed something while the save was in flight — chain another.
                        RaiseSaveSettings();
                    }
                    else
                    {
                        _savePending = false;
                    }
                });
        }

        private void AttachPanelHandlers()
        {
            if (_allocationResult == null) return;
            foreach (var panel in _allocationResult.AllPanels)
            {
                panel.PropertyChanged += OnPanelPropertyChanged;
            }
        }

        private void DetachPanelHandlers()
        {
            if (_allocationResult == null) return;
            foreach (var panel in _allocationResult.AllPanels)
            {
                panel.PropertyChanged -= OnPanelPropertyChanged;
            }
        }

        private void OnPanelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PanelResult.SelectedSpecialDevice)
                || e.PropertyName == nameof(PanelResult.SelectedSpecialDevice2))
            {
                RebuildSubsystemDetails();
                RebuildLinkAssignments();
                RebuildBom();
                SaveSettings();
            }
            else if (e.PropertyName == nameof(PanelResult.SelectedPanelSize))
            {
                // Capture the user's panel size override, then defer rebuild so it runs
                // after WPF finishes processing the ComboBox selection change event.
                // Rebuilding synchronously destroys the visual tree mid-event, causing crashes.
                if (sender is PanelResult panel)
                {
                    _panelSizeOverrides[panel.PanelName] = panel.SelectedPanelSize;
                    SaveSettings();
                    Dispatcher.CurrentDispatcher.BeginInvoke(BuildPanelBreakdown, DispatcherPriority.Background);
                }
            }
        }

        /// <summary>
        /// Label each compartment that holds a subsystem device with what it serves — for DMX, its
        /// control zones. "DMX" alone tells a reviewer nothing about whether the right zones are
        /// covered; the zone names are the thing they can actually check against the drawings.
        ///
        /// Every panel is rewritten, including the ones that clear, so removing a device removes its
        /// caption instead of stranding it under a compartment that no longer holds it.
        /// </summary>
        private void RebuildSubsystemDetails()
        {
            if (_allocationResult == null) return;

            foreach (var panel in _allocationResult.AllPanels)
            {
                panel.SpecialDeviceDetail = DetailFor(panel.SelectedSpecialDevice);
                panel.SpecialDeviceDetail2 = panel.HasDualSpecialCompartment
                    ? DetailFor(panel.SelectedSpecialDevice2)
                    : "";
            }
        }

        /// <summary>The served-zone caption for a compartment selection, or empty when no subsystem
        /// speaks for it. A subsystem that could not solve says so here too — the compartment is where
        /// the user is looking when they wonder why the count seems wrong.</summary>
        private string DetailFor(string selectedDevice)
        {
            if (_subsystemDemands == null || string.IsNullOrEmpty(selectedDevice)) return "";

            foreach (var demand in _subsystemDemands)
            {
                if (demand == null
                    || !string.Equals(demand.Subsystem, selectedDevice, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (demand.HasDiagnostic) return "unsolved";
                if (demand.ServedZones.Count == 0) return "";

                return string.Join(", ", demand.ServedZones);
            }
            return "";
        }

        private void RebuildBom()
        {
            if (_allocationResult == null || _currentBrand == null)
            {
                BomItems = null;
                return;
            }

            var bom = ControlBomBuilder.Build(_allocationResult.AllPanels, _currentBrand, BuildBomExtras());
            BomItems = new ObservableCollection<BomLineItem>(bom);
        }

        /// <summary>The non-panel BOM inputs this window holds. The audience is the design surface:
        /// this is where a processor shortfall — or a DMX design that will not solve — has to be
        /// visible, because this is where it gets fixed.</summary>
        private BomExtras BuildBomExtras() => new BomExtras
        {
            KeypadCount = _keypadCount,
            TwoGangKeypadCount = _twoGangKeypadCount,
            HybridRepeaterCount = _hybridRepeaterCount,
            HybridRepeaterPartNumber = _hybridRepeaterPartNumber,
            SubsystemDemands = _subsystemDemands,
            Audience = BomAudience.DesignSurface
        };

        private void RebuildLinkAssignments()
        {
            if (_allocationResult == null) return;

            bool isLutron = string.Equals(_currentBrand?.Name, "Lutron", StringComparison.OrdinalIgnoreCase);
            var allPanels = _allocationResult.AllPanels;

            // Set IsProcessor on each panel
            foreach (var panel in allPanels)
            {
                bool hasProc = panel.HasSpecialCompartment
                    && string.Equals(panel.SelectedSpecialDevice, "Processor", StringComparison.OrdinalIgnoreCase);
                bool hasProc2 = panel.HasDualSpecialCompartment
                    && string.Equals(panel.SelectedSpecialDevice2, "Processor", StringComparison.OrdinalIgnoreCase);
                panel.IsProcessor = hasProc || hasProc2;
            }

            if (!isLutron)
            {
                // Crestron: clear link data
                foreach (var panel in allPanels)
                {
                    panel.Link1 = null;
                    panel.Link2 = null;
                }
                ProcessorDisplays = new ObservableCollection<PanelResult>();
                return;
            }

            // Build ProcessorLink objects for each processor panel
            var processorPanels = allPanels.Where(p => p.IsProcessor).ToList();
            foreach (var proc in processorPanels)
            {
                if (proc.Link1 == null || proc.Link1.ProcessorPanelName != proc.PanelName)
                    proc.Link1 = new ProcessorLink { ProcessorPanelName = proc.PanelName, LinkNumber = 1 };
                if (proc.Link2 == null || proc.Link2.ProcessorPanelName != proc.PanelName)
                    proc.Link2 = new ProcessorLink { ProcessorPanelName = proc.PanelName, LinkNumber = 2 };
            }

            // Clear Link1/Link2 on non-processor panels
            foreach (var panel in allPanels.Where(p => !p.IsProcessor))
            {
                panel.Link1 = null;
                panel.Link2 = null;
            }

            // Run auto-assignment and aggregate
            LinkAssignmentService.AssignAndAggregate(allPanels, _keypadCount + _twoGangKeypadCount * 2, _hybridRepeaterCount);

            // Rebuild processor displays for sidebar
            ProcessorDisplays = new ObservableCollection<PanelResult>(processorPanels);
        }

        private void SaveSpecialDeviceSelections()
        {
            if (_allocationResult == null) return;
            foreach (var panel in _allocationResult.AllPanels)
            {
                if (panel.HasSpecialCompartment
                    && !string.IsNullOrEmpty(panel.SelectedSpecialDevice)
                    && panel.SelectedSpecialDevice != "Empty")
                {
                    _specialDeviceSelections[panel.PanelName] = panel.SelectedSpecialDevice;
                }
                else if (panel.HasSpecialCompartment)
                {
                    _specialDeviceSelections.Remove(panel.PanelName);
                }

                // Second slot (dual compartment panels like LV21)
                string slot2Key = panel.PanelName + "#2";
                if (panel.HasDualSpecialCompartment
                    && !string.IsNullOrEmpty(panel.SelectedSpecialDevice2)
                    && panel.SelectedSpecialDevice2 != "Empty")
                {
                    _specialDeviceSelections[slot2Key] = panel.SelectedSpecialDevice2;
                }
                else
                {
                    _specialDeviceSelections.Remove(slot2Key);
                }
            }
        }

        private void RestoreSpecialDeviceSelections()
        {
            if (_allocationResult == null) return;
            foreach (var panel in _allocationResult.AllPanels)
            {
                if (panel.HasSpecialCompartment
                    && _specialDeviceSelections.TryGetValue(panel.PanelName, out var device)
                    && panel.SpecialDeviceOptions != null
                    && panel.SpecialDeviceOptions.Contains(device))
                {
                    panel.SelectedSpecialDevice = device;
                }

                // Second slot (dual compartment panels like LV21)
                if (panel.HasDualSpecialCompartment
                    && _specialDeviceSelections.TryGetValue(panel.PanelName + "#2", out var device2)
                    && panel.SpecialDeviceOptions != null
                    && panel.SpecialDeviceOptions.Contains(device2))
                {
                    panel.SelectedSpecialDevice2 = device2;
                }
            }
        }
    }
}
