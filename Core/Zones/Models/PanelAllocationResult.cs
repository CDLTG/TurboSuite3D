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

        /// <summary>The shade (Sivoia QS) panels recommended for this location, appended after the lighting
        /// panels in the letter run (…1-C lighting, 1-D 1-E shades). Deliberately separate from
        /// <see cref="Panels"/>: a shade panel carries a shade fill, not dimming modules, so it must not feed
        /// the module/overcapacity math above. A pure-shade location has these with no <see cref="Panels"/>.</summary>
        public List<ShadePanelResult> ShadePanels { get; set; } = new List<ShadePanelResult>();

        public bool HasShadePanels => ShadePanels.Count > 0;

        /// <summary>Shade panels grouped into bottom-aligned stacks of three, mimicking the field: the first
        /// shade panel opens a new column beside the lighting, the next two stack above it, then a fourth
        /// starts a second column. Each inner list is top-to-bottom render order (1-F, 1-E, 1-D) so a normal
        /// top-down stack puts the lowest-lettered panel at the bottom.</summary>
        public List<List<ShadePanelResult>> ShadeColumns =>
            ShadePanels
                .Select((p, i) => (Panel: p, Index: i))
                .GroupBy(x => x.Index / ShadePanelsPerColumn)
                .Select(g => Enumerable.Reverse(g.Select(x => x.Panel)).ToList())
                .ToList();

        /// <summary>Max shade panels stacked in one column before wrapping to the next — a fixed 3-cap,
        /// which fits within a PD4-or-larger location row without growing it.</summary>
        public const int ShadePanelsPerColumn = 3;
    }

    /// <summary>One recommended QSPS-10PNL in a shade location — the visualizer twin of a dimmer
    /// <see cref="PanelResult"/>, but with a shade fill (n / 10) instead of dimming modules and no size,
    /// compartment, or override controls (a shade panel is a fixed ten-output device, a pure
    /// recommendation). Its <see cref="ShadeCount"/> comes from <c>ShadeSolver.PanelFills</c>, so the tiles
    /// drawn total exactly what the BOM orders.</summary>
    public class ShadePanelResult
    {
        public int LocationNumber { get; set; }
        public string PanelName { get; set; }          // e.g. "1-D"
        public int ShadeCount { get; set; }            // shades landed on this panel (the fill)
        public int Capacity { get; set; } = 10;        // QSPS-10PNL outputs
        public string FillDisplay => $"{ShadeCount} / {Capacity}";
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

        // Link budget from this panel's own modules — dimming modules are QS devices and their slots are
        // loads. A subsystem-placed module (a DALI DIN module) is EXCLUDED: it occupies a panel slot but
        // its QS device + legs are reported job-wide through its subsystem's demand (LinkDevices/LinkLoads)
        // and folded in as floating demand by ControlLinkPacker. Counting it here too would double it.
        public int DeviceCount => Modules.Count(m => !m.OrderedBySubsystem);
        public int LoadCount => Modules.Where(m => !m.OrderedBySubsystem).Sum(m => m.ModuleCapacity);

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class ModuleResult
    {
        public string DimmingType { get; set; }
        public string PartNumber { get; set; }
        public int ModuleCapacity { get; set; }
        public List<string> CircuitNumbers { get; set; } = new List<string>();

        /// <summary>When set, the "used" figure shown for this module instead of the circuit count. Used by
        /// the DALI DIN module, whose usage is its bus <i>load</i> count (e.g. 33 / 64), not a circuit tally
        /// — its single <see cref="CircuitNumbers"/> entry is the loop label, not a load.</summary>
        public int? UsedSlotsOverride { get; set; }

        public int UsedSlots => UsedSlotsOverride ?? CircuitNumbers.Count;
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

        /// <summary>
        /// Set only on modules from the merged Relay+0-10V packing pool, where <see cref="DimmingType"/>
        /// is a synthetic sort/selection key ("0-10V", so part number, amp limits, BOM rank, and color
        /// stay correct) rather than a display truth. When true, <see cref="TypeLabel"/> reads the actual
        /// protocol(s) off the slots instead of the module key — otherwise a pure-relay module in the pool
        /// would mislabel itself "0-10V".
        /// </summary>
        public bool LabelFromSlotProtocols { get; set; }

        /// <summary>
        /// The label shown on the module tile's top line. For a normal module (the overwhelmingly common
        /// case) this is the module's <see cref="DimmingType"/> — so an MLV load riding an ELV module
        /// still reads "ELV", exactly as before. For a merged Relay+0-10V module
        /// (<see cref="LabelFromSlotProtocols"/>) it is the distinct per-slot protocols joined
        /// "RELAY / 0-10V" (relay first) — or the single protocol when the module happens to be pure —
        /// because the module key "0-10V" alone would hide the relay loads sharing it.
        /// </summary>
        public string TypeLabel
        {
            get
            {
                if (!LabelFromSlotProtocols) return DimmingType;

                var distinct = SlotProtocols
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (distinct.Count == 0) return DimmingType;   // empty/padding module in the pool
                distinct.Sort((a, b) => TypeLabelRank(a).CompareTo(TypeLabelRank(b)));
                return string.Join(" / ", distinct);
            }
        }

        private static int TypeLabelRank(string protocol)
        {
            if (protocol.IndexOf("RELAY", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            if (protocol.IndexOf("0-10", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            return 2;
        }

        /// <summary>True if any slot or the module total exceeds amp limits.</summary>
        public bool IsOverloaded { get; set; }

        public double TotalAmps => SlotAmps.Sum();

        /// <summary>
        /// This module was placed by a control subsystem (the DALI DIN module), not derived from dimming
        /// circuits. It occupies a panel slot — so it counts toward <see cref="PanelResult.TotalModuleCount"/>,
        /// <see cref="PanelResult.EmptySlots"/> and the panel-count recommendation like any module — but it
        /// is <b>ordered and link-budgeted by its subsystem's job-wide demand</b>, so it is excluded from
        /// the BOM roll-up (<see cref="PanelAllocationService.GroupModulesByPartNumber"/>) and from
        /// <see cref="PanelResult.DeviceCount"/>/<see cref="PanelResult.LoadCount"/>. Its slot is labeled by
        /// its loop (via <see cref="CircuitNumbers"/>), not by circuits.
        /// </summary>
        public bool OrderedBySubsystem { get; set; }
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
