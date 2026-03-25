#nullable disable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace TurboSuite.Zones.Models
{
    public class PanelAllocationResult
    {
        public List<LocationResult> Locations { get; set; } = new List<LocationResult>();
        public List<PanelResult> AllPanels => Locations.SelectMany(l => l.Panels).ToList();
    }

    public class LocationResult
    {
        public int LocationNumber { get; set; }
        public List<PanelResult> Panels { get; set; } = new List<PanelResult>();
        public int TotalModules { get; set; }
        public int TotalCapacity => Panels.Sum(p => p.PanelCapacity);
        public bool IsOverCapacity => TotalModules > TotalCapacity;
    }

    public class PanelResult : INotifyPropertyChanged
    {
        private string _selectedSpecialDevice = "";
        private string _selectedSpecialDevice2 = "";
        private bool _isProcessor;
        private ProcessorLink _link1;
        private ProcessorLink _link2;
        private int _selectedPanelSize;

        public string PanelName { get; set; }
        public int PanelCapacity => _selectedPanelSize;

        public int SelectedPanelSize
        {
            get => _selectedPanelSize;
            set
            {
                if (_selectedPanelSize == value) return;
                _selectedPanelSize = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedPanelSize)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PanelCapacity)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EmptySlots)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSpecialCompartment)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDualSpecialCompartment)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisibleModulesBottomUp)));
            }
        }
        public List<ModuleResult> Modules { get; set; } = new List<ModuleResult>();
        public int TotalModuleCount => Modules.Count;
        public int EmptySlots => Math.Max(0, PanelCapacity - TotalModuleCount);
        public List<ModuleResult> VisibleModulesBottomUp =>
            Enumerable.Reverse(Modules.Take(PanelCapacity)).ToList();

        public HashSet<int> SpecialCompartmentPanelSizes { get; set; }
        public HashSet<int> DualCompartmentPanelSizes { get; set; }
        public List<PanelSizeOption> AvailablePanelSizes { get; set; }
        public bool HasSpecialCompartment => SpecialCompartmentPanelSizes != null
            && SpecialCompartmentPanelSizes.Contains(_selectedPanelSize);
        public bool HasDualSpecialCompartment => DualCompartmentPanelSizes != null
            && DualCompartmentPanelSizes.Contains(_selectedPanelSize);
        public List<string> SpecialDeviceOptions { get; set; }
        public Dictionary<string, string> SpecialDevicePartNumbers { get; set; }

        public string SelectedSpecialDevice
        {
            get => _selectedSpecialDevice;
            set
            {
                if (_selectedSpecialDevice == value) return;
                _selectedSpecialDevice = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSpecialDevice)));
            }
        }

        public string SelectedSpecialDevice2
        {
            get => _selectedSpecialDevice2;
            set
            {
                if (_selectedSpecialDevice2 == value) return;
                _selectedSpecialDevice2 = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSpecialDevice2)));
            }
        }

        // Processor capacity bars
        public bool IsProcessor
        {
            get => _isProcessor;
            set
            {
                if (_isProcessor == value) return;
                _isProcessor = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsProcessor)));
            }
        }

        public ProcessorLink Link1
        {
            get => _link1;
            set
            {
                _link1 = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Link1)));
            }
        }

        public ProcessorLink Link2
        {
            get => _link2;
            set
            {
                _link2 = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Link2)));
            }
        }

        public int DeviceCount => Modules.Count;
        public int LoadCount => Modules.Sum(m => m.ModuleCapacity);

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class ModuleResult
    {
        public string DimmingType { get; set; }
        public string PartNumber { get; set; }
        public int ModuleCapacity { get; set; }
        public List<string> CircuitNumbers { get; set; } = new List<string>();
        public int UsedSlots => CircuitNumbers.Count;
        public string CircuitNumbersDisplay => string.Join(", ", CircuitNumbers);
    }

    public class PanelSizeOption
    {
        public int Size { get; set; }
        public string DisplayName { get; set; }
    }

    public class BomLineItem
    {
        public int Quantity { get; set; }
        public string PartNumber { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public bool IsHeader { get; set; }
        public bool IsWarning { get; set; }
    }
}
