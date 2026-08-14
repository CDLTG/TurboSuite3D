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
        private bool _allowRelayZeroTenPacking;
        private PanelAllocationResult _allocationResult;
        private ObservableCollection<LocationDisplayViewModel> _locationDisplays;
        private ObservableCollection<BomLineItem> _bomItems;
        private readonly Dictionary<string, string> _specialDeviceSelections = new Dictionary<string, string>();
        private readonly Dictionary<string, int> _panelSizeOverrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly int _keypadCount;
        private readonly int _twoGangKeypadCount;
        private readonly int _wirelessDeviceCount;
        private readonly IReadOnlyList<ControlDeviceTally> _keypadTallies;
        private readonly ControlDeviceGroup _hybridRepeaters;

        /// <summary>What the control subsystems (TurboDMX today) reported at window open. Read once —
        /// re-solving DMX on every panel-size tweak would be wasted work, and the DMX design cannot
        /// change while this window is the one in front of the user.</summary>
        private readonly IReadOnlyList<ControlSubsystemDemand> _subsystemDemands;

        /// <summary>DALI modules to place, keyed by the ZONE N the designer assigned each loop to (built
        /// shim-side from the persisted DALI loops at window open — the same read-once rule as the demands).
        /// Placement only; the DALI order/link budget rides <see cref="_subsystemDemands"/>.</summary>
        private readonly IReadOnlyDictionary<int, IReadOnlyList<DaliPanelModule>> _daliModulesByZone;

        /// <summary>Per-location shade tallies read once at window open (like the demands). The Panel
        /// Breakdown draws shade panels per location from these; the shade BOM/link demand rides
        /// <see cref="_subsystemDemands"/>, both derived from the same ShadeSolver per-location count.</summary>
        private readonly IReadOnlyList<ShadeLocationTally> _shadeLocations;
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
            KeypadCounts keypadCounts,
            ControlDeviceGroup hybridRepeaters,
            PanelSettings savedSettings,
            IRevitWorkQueue workQueue, IPanelSettingsStore settingsStore,
            IReadOnlyList<ControlSubsystemDemand> subsystemDemands = null,
            IReadOnlyDictionary<int, IReadOnlyList<DaliPanelModule>> daliModulesByZone = null,
            IReadOnlyList<ShadeLocationTally> shadeLocations = null)
        {
            _workQueue = workQueue;
            _settingsStore = settingsStore;
            _subsystemDemands = subsystemDemands;
            _daliModulesByZone = daliModulesByZone;
            _shadeLocations = shadeLocations;
            keypadCounts ??= new KeypadCounts();
            _keypadCount = keypadCounts.Regular;
            _twoGangKeypadCount = keypadCounts.TwoGang;
            _wirelessDeviceCount = keypadCounts.WirelessDevices;
            _keypadTallies = keypadCounts.Tallies;
            _hybridRepeaters = hybridRepeaters;

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
                {
                    // The Relay+0-10V toggle is only meaningful when NOT on a dedicated relay module.
                    OnPropertyChanged(nameof(CanMergeRelayZeroTen));
                    BuildPanelBreakdown();
                }
            }
        }

        /// <summary>Pack RELAY and 0-10V loads onto one LQSE-4T5 module instead of splitting them.
        /// The allocator ignores this whenever the two don't share a part number (dedicated relay module,
        /// or Crestron), so no guard is needed here beyond the greyed-out UI.</summary>
        public bool AllowRelayZeroTenPacking
        {
            get => _allowRelayZeroTenPacking;
            set
            {
                if (SetProperty(ref _allowRelayZeroTenPacking, value))
                    BuildPanelBreakdown();
            }
        }

        /// <summary>The Relay+0-10V toggle is offered only when a dedicated relay module is NOT selected —
        /// the LQSE-4S8 is a physically distinct module the two loads cannot share. Bound to the checkbox's
        /// IsEnabled; visibility is Lutron-only, matching the dedicated toggle.</summary>
        public bool CanMergeRelayZeroTen => !_useDedicatedRelayModule;

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

        private ObservableCollection<ProcessorInstance> _processorDisplays;
        public ObservableCollection<ProcessorInstance> ProcessorDisplays
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
                circuitData, _currentBrand, _panelSizeOverrides, _subsystemDemands, _daliModulesByZone,
                _allowRelayZeroTenPacking, _shadeLocations);

            AllocationResult = result;
            UnassignedCircuits = new ObservableCollection<ZonesCircuitData>(unassigned);

            // Restore special device selections (no auto-lock — processor is manual)
            RestoreSpecialDeviceSelections();
            AttachPanelHandlers();
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
                _allowRelayZeroTenPacking = settings.AllowRelayZeroTenPacking;
                OnPropertyChanged(nameof(SelectedBrandName));
                OnPropertyChanged(nameof(IsLutronSelected));
                OnPropertyChanged(nameof(UseDedicatedRelayModule));
                OnPropertyChanged(nameof(AllowRelayZeroTenPacking));
                OnPropertyChanged(nameof(CanMergeRelayZeroTen));

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
                UseDedicatedRelayModule = _useDedicatedRelayModule,
                AllowRelayZeroTenPacking = _allowRelayZeroTenPacking
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
            WirelessDeviceCount = _wirelessDeviceCount,
            KeypadTallies = _keypadTallies,
            HybridRepeaters = _hybridRepeaters,
            SubsystemDemands = _subsystemDemands,
            Audience = BomAudience.DesignSurface
        };

        private void RebuildLinkAssignments()
        {
            if (_allocationResult == null) return;

            bool isLutron = string.Equals(_currentBrand?.Name, "Lutron", StringComparison.OrdinalIgnoreCase);
            var allPanels = _allocationResult.AllPanels;

            // Set IsProcessor on each panel — a per-panel "hosts a processor" flag. The sidebar itself
            // is built per-slot below (an LV21 with two processors is two instances), but this stays a
            // useful panel-level fact.
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
                ProcessorDisplays = new ObservableCollection<ProcessorInstance>();
                return;
            }

            // Build one processor instance per placed "Processor" slot and pack onto their links. Same
            // inputs as the BOM, on purpose — the bars and the processor recommendation are two questions
            // to one packer, counted per-slot just as the supply sizer counts them.
            ProcessorDisplays = new ObservableCollection<ProcessorInstance>(
                LinkAssignmentService.BuildProcessorInstances(allPanels, BuildBomExtras(), _currentBrand));
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
