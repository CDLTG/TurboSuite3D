#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Zones.Models
{
    /// <summary>
    /// What a control subsystem needs from the panel and the control link, as reported by whoever
    /// actually solves that subsystem.
    ///
    /// The shape both DMX and (later) DALI fit: the designer declares groupings, a solver derives the
    /// device count, and the panel breakdown plus the BOM consume that count <b>without recomputing
    /// it</b>. TurboDMX is a complete instance of it already — <c>DmxBill.InterfaceCount</c> is the
    /// QSE-CI-DMX quantity, from real channel math.
    ///
    /// <b>This is a requirement, not an order.</b> Where a part has a home the designer picks — a
    /// panel's LV compartment — the placement is what gets ordered, and this demand becomes the
    /// "(1 of 4 placed)" annotation that tells them to go site the rest. Siting hardware is a
    /// judgement no solver can make, and the designer can always act on the signal (an LV21 override
    /// frees two compartments and the allocator re-homes the displaced modules). Only a part with no
    /// compartment to be placed into is ordered straight from here.
    ///
    /// A provider that cannot solve reports zero parts and a <see cref="Diagnostic"/>, never a guess.
    /// </summary>
    public sealed class ControlSubsystemDemand
    {
        public ControlSubsystemDemand(
            string subsystem,
            IReadOnlyList<DemandPart>? parts = null,
            int linkDevices = 0,
            int linkLoads = 0,
            IReadOnlyList<string>? servedZones = null,
            string? diagnostic = null)
        {
            Subsystem = subsystem;
            Parts = parts ?? new List<DemandPart>();
            LinkDevices = linkDevices;
            LinkLoads = linkLoads;
            ServedZones = servedZones ?? new List<string>();
            Diagnostic = diagnostic;
        }

        /// <summary>Which subsystem this is — "DMX", later "DALI". Identifies the demand in the UI and
        /// keys the per-subsystem BOM lines.</summary>
        public string Subsystem { get; }

        /// <summary>The parts the job needs, already counted. Empty when the subsystem is absent from
        /// the job or could not be solved (see <see cref="Diagnostic"/>). Whether a part is ordered at
        /// this quantity depends on <see cref="DemandPart.Mount"/> — see the class summary.</summary>
        public IReadOnlyList<DemandPart> Parts { get; }

        /// <summary>Devices this subsystem consumes off the control link's device budget. For DMX this
        /// is the interface count: <i>"Each QSE-CI-DMX control interface counts as 1 QS device and 0
        /// zones"</i> — so devices and loads are independent here, not two views of one number.</summary>
        public int LinkDevices { get; }

        /// <summary>Switch legs consumed off the control link's load budget. For DMX, <i>"each DMX
        /// channel = 1 switch leg"</i>, so this is the total channel count across every interface.</summary>
        public int LinkLoads { get; }

        /// <summary>The control zones this demand serves, for display under the compartment. Names as
        /// the designer authored them.</summary>
        public IReadOnlyList<string> ServedZones { get; }

        /// <summary>Why the demand is zero or unsolvable, in the subsystem's own words — surfaced as a
        /// BOM warning line. Null when the demand is a clean solve (including a clean zero).</summary>
        public string? Diagnostic { get; }

        /// <summary>True when the provider had something to say about why it could not solve.</summary>
        public bool HasDiagnostic => !string.IsNullOrWhiteSpace(Diagnostic);

        /// <summary>An empty demand carrying only a reason. The shape a provider returns when the
        /// subsystem is mid-design: the BOM still builds, with the reason attached.</summary>
        public static ControlSubsystemDemand Unsolvable(string subsystem, string diagnostic) =>
            new ControlSubsystemDemand(subsystem, diagnostic: diagnostic);

        /// <summary>A clean nothing — the subsystem is simply not in this job. Distinct from
        /// <see cref="Unsolvable"/>: silent, with nothing for the user to fix.</summary>
        public static ControlSubsystemDemand None(string subsystem) =>
            new ControlSubsystemDemand(subsystem);
    }

    /// <summary>One part a subsystem needs, already counted.</summary>
    public sealed class DemandPart
    {
        public DemandPart(string partNumber, int quantity, DemandMount mount, string? description = null)
        {
            PartNumber = partNumber;
            Quantity = quantity;
            Mount = mount;
            Description = description;
        }

        public string PartNumber { get; }
        public int Quantity { get; }
        public DemandMount Mount { get; }

        /// <summary>Overrides the brand's catalog description when the subsystem knows better. Null ⇒
        /// look the part number up in <see cref="BrandConfig"/> like any other line.</summary>
        public string? Description { get; }
    }

    /// <summary>Where a demanded part physically lands — which is what decides whether it competes for
    /// panel capacity.</summary>
    public enum DemandMount
    {
        /// <summary>The panel's low-voltage compartment (QSE-CI-DMX). Does not consume DIN slots, so
        /// panel allocation is untouched — but the designer picks which compartment, so the ORDERED
        /// quantity comes from that placement and this demand only annotates it.</summary>
        LvCompartment,

        /// <summary>A DIN slot alongside the dimming modules (the DALI module, when it lands). This is
        /// the mount that forces the allocator to count externally-supplied modules against panel
        /// capacity.</summary>
        DinSlot,

        /// <summary>Lives outside the control panel entirely. Ordered, but competes for nothing.</summary>
        External
    }
}
