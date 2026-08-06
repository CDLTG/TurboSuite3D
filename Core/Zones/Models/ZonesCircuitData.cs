#nullable disable
using TurboSuite.Abstractions;

namespace TurboSuite.Zones.Models
{
    public enum LabelSource
    {
        CircuitComments,
        FixtureComments,
        Fallback,
        None
    }

    /// <summary>
    /// How a circuit's fixtures resolved to a control-module type (see DimmingModuleResolver).
    /// Only <see cref="Allocatable"/> circuits reach panel allocation; the rest are excluded,
    /// and the reason decides whether that exclusion is worth telling the user about.
    /// </summary>
    public enum DimmingResolveOutcome
    {
        /// <summary>Rides a control module — allocate on DimmingType.</summary>
        Allocatable,

        /// <summary>Deliberately module-less (WIFI). Excluded silently, like a switch-wired circuit.</summary>
        NoModuleByDesign,

        /// <summary>DALI/DMX — a real module TurboSuite does not allocate yet. Flagged.</summary>
        NotYetSupported,

        /// <summary>Nothing declared, or an off-vocabulary value. An authoring gap — flagged.</summary>
        NoProtocol
    }

    public class ZonesCircuitData
    {
        public ElementRef CircuitId { get; set; }
        public string CircuitNumber { get; set; }

        /// <summary>The BrandConfig module key to allocate on. Empty unless
        /// <see cref="DimmingOutcome"/> is <see cref="DimmingResolveOutcome.Allocatable"/>.</summary>
        public string DimmingType { get; set; }

        /// <summary>The raw Dimming Protocol as authored ("MLV", "ELV; 0-10V"), kept separate from
        /// <see cref="DimmingType"/> so neither field has to mean two things. Shown in the
        /// Unassigned list, where a benched DALI circuit must read "DALI" rather than blank.</summary>
        public string DimmingProtocolDisplay { get; set; }

        public DimmingResolveOutcome DimmingOutcome { get; set; }
        public string RoomName { get; set; }
        public string CurrentLoadName { get; set; }
        public string CircuitComments { get; set; }
        public string FixtureComments { get; set; }
        public string LoadClassificationName { get; set; }
        public string PanelName { get; set; }
        public string RoomOverride { get; set; }
        public string UpdatedLoadName { get; set; }
        public LabelSource LabelSource { get; set; }
        public bool IsWiredToSwitch { get; set; }
        public double ApparentLoadVA { get; set; }
    }
}
