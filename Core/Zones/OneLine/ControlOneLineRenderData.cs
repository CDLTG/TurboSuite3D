#nullable enable
using System.Collections.Generic;
using TurboSuite.Shared.Constants;

namespace TurboSuite.Zones.OneLine
{
    /// <summary>
    /// The resolved render content for one power panel, keyed by panel name — what the planner needs to draw
    /// a <see cref="ControlPanelNode"/> and nothing more. The SHIM builds these at draw time from
    /// <c>PanelResult</c> + <c>BrandConfig</c> (module part numbers from <c>ModuleResult.PartNumber</c>, the
    /// panel part number from <c>BrandConfig.PanelPartNumbers[size]</c>, LV-slot labels from
    /// <c>PanelResult.SpecialDevicePartNumbers</c> / the processor), so the Core planner stays pure and free
    /// of both models — it joins a <c>PackedLinkUnit.Name</c> to one of these and lays it out.
    /// </summary>
    public sealed class ControlPanelRenderData
    {
        public ControlPanelRenderData(string name, string partNumber, string fillText,
            IReadOnlyList<string?> moduleTiles, IReadOnlyList<string> lvSlots, bool hostsProcessor,
            string? enclosureRole = null)
        {
            Name = name;
            PartNumber = partNumber;
            FillText = fillText;
            ModuleTiles = moduleTiles;
            LvSlots = lvSlots;
            HostsProcessor = hostsProcessor;
            EnclosureRole = enclosureRole ?? Roles.ControlPanelDetail;
        }

        public string Name { get; }
        public string PartNumber { get; }
        public string FillText { get; }

        /// <summary>One entry per module slot, top→bottom: the module's catalog part number, or null = empty.</summary>
        public IReadOnlyList<string?> ModuleTiles { get; }

        /// <summary>The LV-compartment labels, top→bottom (PD8/PD9 = 1 entry, LV21 = 2): a processor part
        /// number, "EMPTY", or an IO/interface device. Empty list for a panel with no LV compartment.</summary>
        public IReadOnlyList<string> LvSlots { get; }

        /// <summary>This panel hosts the processor — the planner treats it as the head of its links. Set from
        /// the allocation (<c>PanelResult.IsProcessor</c>), not inferred from a part number.</summary>
        public bool HostsProcessor { get; }

        /// <summary>The TurboSuite Role of the enclosure family to place (branded — PD8/PD9, LV21, …). The
        /// renderer resolves whatever family advertises this role, so a new brand/size is additive.</summary>
        public string EnclosureRole { get; }
    }
    // Shade panels need no render-data lookup — the planner renders them straight off their PackedLinkUnit
    // (name + Loads = motor count), so there is deliberately no ControlShadeRenderData type.
}
