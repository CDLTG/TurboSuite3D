#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using TurboSuite.Shared.ViewModels;
using TurboSuite.Zones.Views;

namespace TurboSuite.Zones.ViewModels
{
    public class PanelBreakdownTabViewModel : ViewModelBase
    {
        private static string[] ModuleTypeOrder => PanelAllocationService.ModuleTypeOrder;

        private string _selectedBrandName;
        private PanelAllocationResult _allocationResult;
        private ObservableCollection<LocationDisplayViewModel> _locationDisplays;
        private ObservableCollection<BomLineItem> _bomItems;
        private readonly Dictionary<string, string> _specialDeviceSelections = new Dictionary<string, string>();
        private readonly int _keypadCount;
        private readonly int _twoGangKeypadCount;
        private readonly int _hybridRepeaterCount;
        private readonly string _hybridRepeaterPartNumber;
        private readonly Document _doc;
        private BrandConfig _currentBrand;
        private ObservableCollection<ZonesCircuitData> _unassignedCircuits;
        private readonly HashSet<string> _knownPanelNames;
        private readonly Dictionary<string, string> _panelCatalogNumbers;

        public PanelBreakdownTabViewModel(Document doc, List<ZonesCircuitData> circuits,
            int keypadCount = 0, int twoGangKeypadCount = 0,
            int hybridRepeaterCount = 0, string hybridRepeaterPartNumber = null,
            Dictionary<string, string> panelCatalogNumbers = null)
        {
            _doc = doc;
            _panelCatalogNumbers = panelCatalogNumbers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _keypadCount = keypadCount;
            _twoGangKeypadCount = twoGangKeypadCount;
            _hybridRepeaterCount = hybridRepeaterCount;
            _hybridRepeaterPartNumber = hybridRepeaterPartNumber;

            // Capture all panel names at startup so panels are never lost from the display
            _knownPanelNames = new HashSet<string>(
                circuits
                    .Where(c => !string.IsNullOrWhiteSpace(c.PanelName))
                    .Select(c => c.PanelName),
                StringComparer.OrdinalIgnoreCase);

            Circuits = new ObservableCollection<ZonesCircuitViewModel>(
                circuits.OrderBy(c => c.CircuitNumber).Select(c => new ZonesCircuitViewModel(c)));

            BrandNames = new List<string> { "Lutron", "Crestron" };
            _selectedBrandName = "Lutron";

            LoadSavedSettings();
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
                    BuildPanelBreakdown();
                }
            }
        }

        public bool IsLutronSelected => string.Equals(_selectedBrandName, "Lutron", StringComparison.OrdinalIgnoreCase);

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
                : BrandConfig.Lutron;

            var circuitData = Circuits.Select(c => c.Data).ToList();

            var (result, unassigned) = PanelAllocationService.BuildPanelBreakdown(
                circuitData, _currentBrand, _panelCatalogNumbers, _specialDeviceSelections,
                _knownPanelNames);

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

        private void LoadSavedSettings()
        {
            var settings = ZonesPanelSettingsStorageService.Load(_doc);
            if (settings != null)
            {
                _selectedBrandName = settings.Brand ?? "Lutron";
                OnPropertyChanged(nameof(SelectedBrandName));
                OnPropertyChanged(nameof(IsLutronSelected));

                // Restore special device selections
                foreach (var kvp in settings.SpecialDeviceSelections)
                    _specialDeviceSelections[kvp.Key] = kvp.Value;
            }

            // Auto-build on load
            BuildPanelBreakdown();
        }

        private void SaveSettings()
        {
            var settings = new PanelSettings
            {
                Brand = _selectedBrandName
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
                }
            }

            ZonesPanelSettingsStorageService.Save(_doc, settings);
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
            if (e.PropertyName == nameof(PanelResult.SelectedSpecialDevice))
            {
                RebuildLinkAssignments();
                RebuildBom();
                SaveSettings();
            }
        }

        private void RebuildBom()
        {
            if (_allocationResult == null || _currentBrand == null)
            {
                BomItems = null;
                return;
            }

            var bom = new List<BomLineItem>();
            var allPanels = _allocationResult.AllPanels;

            // Count processors placed in panel dropdowns
            int processorCount = allPanels.Count(p =>
                p.HasSpecialCompartment
                && string.Equals(p.SelectedSpecialDevice, "Processor", StringComparison.OrdinalIgnoreCase));

            // Calculate recommended processors from total device/load requirements
            int recommendedProcessors = CalculateRecommendedProcessors(allPanels);
            int bomProcessorCount = Math.Max(recommendedProcessors, processorCount);

            // --- Processors (always shown — minimum 1 required) ---
            {
                bom.Add(new BomLineItem { IsHeader = true, Category = "Processors", Description = "Processors" });

                string processorPn = _currentBrand.SpecialDevices != null
                    && _currentBrand.SpecialDevices.TryGetValue("Processor", out var ppn) ? ppn : "";
                string description = _currentBrand.GetPartDescription(processorPn);

                bool needsWarning = processorCount < recommendedProcessors;
                if (needsWarning)
                    description += $" ({processorCount} of {recommendedProcessors} placed)";

                bom.Add(new BomLineItem
                {
                    Quantity = bomProcessorCount,
                    PartNumber = processorPn,
                    Description = description,
                    Category = "Processors",
                    IsWarning = needsWarning
                });
            }

            // --- Panels ---
            var panelsBySize = allPanels.GroupBy(p => p.PanelCapacity).OrderByDescending(g => g.Key).ToList();
            if (panelsBySize.Count > 0)
            {
                bom.Add(new BomLineItem { IsHeader = true, Category = "Panels", Description = "Panels" });

                foreach (var group in panelsBySize)
                {
                    string partNumber = _currentBrand.PanelPartNumbers.TryGetValue(group.Key, out var pn) ? pn : "";
                    bom.Add(new BomLineItem
                    {
                        Quantity = group.Count(),
                        PartNumber = partNumber,
                        Description = _currentBrand.GetPartDescription(partNumber),
                        Category = "Panels"
                    });
                }
            }

            // --- Modules ---
            var allModules = allPanels.SelectMany(p => p.Modules).ToList();
            if (allModules.Count > 0)
            {
                bom.Add(new BomLineItem { IsHeader = true, Category = "Modules", Description = "Modules" });

                var modulesByType = allModules.GroupBy(m => m.DimmingType).ToList();
                foreach (var typeGroup in ModuleTypeOrder)
                {
                    var group = modulesByType.FirstOrDefault(g =>
                        string.Equals(g.Key, typeGroup, StringComparison.OrdinalIgnoreCase));
                    if (group == null) continue;
                    string modulePn = _currentBrand.GetModulePartNumber(group.Key);
                    bom.Add(new BomLineItem
                    {
                        Quantity = group.Count(),
                        PartNumber = modulePn,
                        Description = _currentBrand.GetPartDescription(modulePn),
                        Category = "Modules"
                    });
                }
                // Any non-standard dimming types
                foreach (var group in modulesByType)
                {
                    bool isStandard = false;
                    foreach (var t in ModuleTypeOrder)
                    {
                        if (string.Equals(group.Key, t, StringComparison.OrdinalIgnoreCase))
                        { isStandard = true; break; }
                    }
                    if (!isStandard)
                    {
                        string modulePn = _currentBrand.GetModulePartNumber(group.Key);
                        bom.Add(new BomLineItem
                        {
                            Quantity = group.Count(),
                            PartNumber = modulePn,
                            Description = _currentBrand.GetPartDescription(modulePn),
                            Category = "Modules"
                        });
                    }
                }
            }

            // --- Accessories ---
            var accessories = new List<BomLineItem>();

            // Power supply: 1 per processor (minimum 1)
            if (!string.IsNullOrEmpty(_currentBrand.PowerSupplyPartNumber))
            {
                accessories.Add(new BomLineItem
                {
                    Quantity = bomProcessorCount,
                    PartNumber = _currentBrand.PowerSupplyPartNumber,
                    Description = _currentBrand.GetPartDescription(_currentBrand.PowerSupplyPartNumber),
                    Category = "Accessories"
                });
            }

            // Wire harnesses (one per panel, by size)
            if (_currentBrand.WireHarnessPartNumbers != null)
            {
                foreach (var group in panelsBySize)
                {
                    if (_currentBrand.WireHarnessPartNumbers.TryGetValue(group.Key, out var harnessPn))
                    {
                        accessories.Add(new BomLineItem
                        {
                            Quantity = group.Count(),
                            PartNumber = harnessPn,
                            Description = _currentBrand.GetPartDescription(harnessPn),
                            Category = "Accessories"
                        });
                    }
                }
            }

            // Special devices from panel selections (Digital I/O, DMX — excludes Processor and Empty)
            if (_currentBrand.SpecialDevices != null)
            {
                var specialCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var panel in allPanels)
                {
                    if (!panel.HasSpecialCompartment) continue;
                    string selected = panel.SelectedSpecialDevice;
                    if (string.IsNullOrEmpty(selected)
                        || string.Equals(selected, "Empty", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(selected, "Processor", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!specialCounts.ContainsKey(selected))
                        specialCounts[selected] = 0;
                    specialCounts[selected]++;
                }

                foreach (var kvp in specialCounts)
                {
                    string partNumber = _currentBrand.SpecialDevices.TryGetValue(kvp.Key, out var spn) ? spn : "";
                    accessories.Add(new BomLineItem
                    {
                        Quantity = kvp.Value,
                        PartNumber = partNumber,
                        Description = _currentBrand.GetPartDescription(partNumber),
                        Category = "Accessories"
                    });
                }
            }

            // Hybrid repeaters (Lutron only)
            if (_hybridRepeaterCount > 0
                && string.Equals(_currentBrand.Name, "Lutron", StringComparison.OrdinalIgnoreCase))
            {
                accessories.Add(new BomLineItem
                {
                    Quantity = _hybridRepeaterCount,
                    PartNumber = _hybridRepeaterPartNumber ?? "",
                    Description = "HWQS Hybrid Wired/Wireless RF System Repeater",
                    Category = "Accessories"
                });
            }

            if (accessories.Count > 0)
            {
                bom.Add(new BomLineItem { IsHeader = true, Category = "Accessories", Description = "Accessories" });
                bom.AddRange(accessories);
            }

            // --- Keypads ---
            if (_keypadCount > 0 || _twoGangKeypadCount > 0)
            {
                bom.Add(new BomLineItem { IsHeader = true, Category = "Keypads", Description = "Keypads" });
                if (_keypadCount > 0)
                {
                    bom.Add(new BomLineItem
                    {
                        Quantity = _keypadCount,
                        PartNumber = "",
                        Description = "Keypad",
                        Category = "Keypads"
                    });
                }
                if (_twoGangKeypadCount > 0)
                {
                    bom.Add(new BomLineItem
                    {
                        Quantity = _twoGangKeypadCount,
                        PartNumber = "",
                        Description = "Two-Gang Keypad",
                        Category = "Keypads"
                    });
                }
            }

            BomItems = new ObservableCollection<BomLineItem>(bom);
        }

        private void RebuildLinkAssignments()
        {
            if (_allocationResult == null) return;

            bool isLutron = string.Equals(_currentBrand?.Name, "Lutron", StringComparison.OrdinalIgnoreCase);
            var allPanels = _allocationResult.AllPanels;

            // Set IsLutron and IsProcessor on each panel
            foreach (var panel in allPanels)
            {
                panel.IsLutron = isLutron;
                panel.IsProcessor = panel.HasSpecialCompartment
                    && string.Equals(panel.SelectedSpecialDevice, "Processor", StringComparison.OrdinalIgnoreCase);
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
            }
        }

        private ICommand _optimizeCommand;
        public ICommand OptimizeCommand => _optimizeCommand ??= new RelayCommand(RunOptimize);

        private void RunOptimize()
        {
            if (_allocationResult == null || _currentBrand == null)
                return;

            // Check for overloaded locations
            var overloadedLocations = _allocationResult.Locations
                .Where(l => l.IsOverCapacity)
                .ToList();
            if (overloadedLocations.Count > 0)
            {
                var names = string.Join(", ", overloadedLocations.Select(l => $"Location {l.LocationNumber}"));
                TaskDialog.Show("TurboZones - Optimize",
                    $"Cannot optimize: the following locations are overloaded:\n{names}\n\n" +
                    "Please add panels or reduce circuits before optimizing.");
                return;
            }

            var circuitData = Circuits.Select(c => c.Data).ToList();

            // 1. Compute redistribution plan (pure computation)
            var plan = CircuitRedistributionService.ComputePlan(
                circuitData, _currentBrand, _allocationResult);

            // 2. Show preview dialog
            var previewVm = new OptimizePreviewViewModel(plan);
            var previewWindow = new OptimizePreviewWindow
            {
                DataContext = previewVm
            };

            if (Application.Current?.Windows.OfType<Window>()
                    .FirstOrDefault(w => w is TurboZonesWindow) is Window parent)
            {
                previewWindow.Owner = parent;
            }

            previewWindow.ShowDialog();

            if (!previewWindow.Confirmed)
                return;

            // 3. Execute Revit transaction
            var moveService = new CircuitMoveService();
            HashSet<ElementId> movedIds;
            try
            {
                movedIds = moveService.ApplyPlan(_doc, plan);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("TurboZones - Optimize",
                    $"Error applying circuit moves:\n{ex.Message}");
                return;
            }

            // 4. Update in-memory PanelName only for circuits that actually moved
            var moveDict = plan.Moves
                .Where(m => movedIds.Contains(m.CircuitId))
                .ToDictionary(m => m.CircuitId, m => m.ToPanel);
            foreach (var vm in Circuits)
            {
                if (moveDict.TryGetValue(vm.Data.CircuitId, out var newPanel))
                    vm.Data.PanelName = newPanel;
            }

            // 5. Rebuild display
            BuildPanelBreakdown();

            int failed = plan.Moves.Count - movedIds.Count;
            string message = $"Optimization complete. {movedIds.Count} circuit(s) moved.";
            if (failed > 0)
                message += $"\n{failed} circuit(s) could not be moved (panel may be full or incompatible).";

            TaskDialog.Show("TurboZones - Optimize", message);
        }

        /// <summary>
        /// Calculates recommended processor count based on total device/load requirements.
        /// Each processor has 2 links (99 devices, 512 loads each).
        /// If hybrid repeaters are present, one or more links are reserved for Clear Connect Type A.
        /// </summary>
        private int CalculateRecommendedProcessors(List<PanelResult> allPanels)
        {
            // Count special devices (Digital I/O, DMX) — each counts as 1 device on a QS link
            int specialDeviceCount = 0;
            foreach (var panel in allPanels)
            {
                if (!panel.HasSpecialCompartment) continue;
                string selected = panel.SelectedSpecialDevice;
                if (string.Equals(selected, "Digital I/O", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(selected, "DMX", StringComparison.OrdinalIgnoreCase))
                    specialDeviceCount++;
            }

            int totalDevices = allPanels.Sum(p => p.DeviceCount)
                + _keypadCount + _twoGangKeypadCount * 2
                + specialDeviceCount;
            int totalLoads = allPanels.Sum(p => p.LoadCount);

            int qsLinksNeeded = Math.Max(
                (int)Math.Ceiling((double)totalDevices / ProcessorLink.MaxDevices),
                (int)Math.Ceiling((double)totalLoads / ProcessorLink.MaxLoads));
            qsLinksNeeded = Math.Max(qsLinksNeeded, 1);

            int ccaLinksNeeded = _hybridRepeaterCount > 0
                ? Math.Max(1, (int)Math.Ceiling((double)_hybridRepeaterCount / ProcessorLink.MaxDevices))
                : 0;

            int totalLinksNeeded = qsLinksNeeded + ccaLinksNeeded;
            return Math.Max(1, (int)Math.Ceiling((double)totalLinksNeeded / 2));
        }
    }
}
