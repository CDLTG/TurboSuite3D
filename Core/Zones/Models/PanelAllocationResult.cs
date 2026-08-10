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

        /// <summary>
        /// The compartment slots this panel actually has — one, or two on a dual-compartment panel
        /// (the LV21) — carrying whatever is selected in each, including "Empty".
        ///
        /// Lives here rather than in a caller because the BOM builder and the link packer both walk
        /// them and must walk them identically: a slot one counts and the other misses is a panel
        /// whose interface is ordered but consumes no link, or vice versa. Yields nothing when the
        /// selected panel size has no compartment, so callers do not repeat that guard.
        /// </summary>
        public IEnumerable<string> CompartmentSlots
        {
            get
            {
                if (!HasSpecialCompartment) yield break;
                yield return _selectedSpecialDevice;
                if (HasDualSpecialCompartment) yield return _selectedSpecialDevice2;
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

        /// <summary>Per-slot amp values, parallel to <see cref="CircuitNumbers"/>.</summary>
        public List<double> SlotAmps { get; set; } = new List<double>();

        /// <summary>
        /// Per-slot raw Dimming Protocol, parallel to <see cref="CircuitNumbers"/>.
        ///
        /// Distinct from <see cref="DimmingType"/> on purpose: that is the MODULE's identity (what
        /// gets ordered and mounted), this is the LOAD's (what the output is configured for). They
        /// coincide for ELV/0-10V/Relay but not for MLV, which dims on an ELV module while needing
        /// the opposite phase mode — a per-output decision the schedule would erase if it printed
        /// the module key on every slot.
        ///
        /// Captured during allocation rather than looked up later, because the panel schedule's
        /// circuit lookup is keyed by circuit NUMBER, which Revit does not guarantee unique
        /// (several circuits can read "&lt;unnamed&gt;").
        /// </summary>
        public List<string> SlotProtocols { get; set; } = new List<string>();

        /// <summary>The protocol to display for a slot, falling back to the module's own type
        /// when a build path did not record one.</summary>
        public string SlotProtocol(int slotIndex)
            => slotIndex >= 0 && slotIndex < SlotProtocols.Count
               && !string.IsNullOrWhiteSpace(SlotProtocols[slotIndex])
                ? SlotProtocols[slotIndex]
                : DimmingType;

        /// <summary>True if any slot or the module total exceeds amp limits.</summary>
        public bool IsOverloaded { get; set; }

        public double TotalAmps => SlotAmps.Sum();
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
