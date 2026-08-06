using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Resolves a circuit's control-module type from its fixtures' "Dimming Protocol" type
    /// parameter — pure and Revit-free, mirroring <see cref="ZonesLabelResolver"/>.
    ///
    /// This replaced the connector-level "Load Classification Abbreviation" as TurboSuite's
    /// module signal. That value lived on a connector inside each family, appeared on no
    /// deliverable, and was routinely missed during family authoring — so a blank silently
    /// dropped a circuit out of panel allocation. Dimming Protocol carries the same information,
    /// prints on the fixture schedule (so it is proofread in the normal course of work), and
    /// already drives TurboDriver's driver selection.
    ///
    /// Module type is a deterministic function of protocol, but NOT the identity function —
    /// MLV dims on an ELV module. That mismatch is why this map exists rather than passing the
    /// protocol straight through as a module key.
    /// </summary>
    public static class DimmingModuleResolver
    {
        private enum Category
        {
            /// <summary>Rides a control module; <see cref="Entry.ModuleKey"/> names which.</summary>
            Module,

            /// <summary>Never rides a control module, by design. Legitimate, not an authoring gap.</summary>
            NoModule,

            /// <summary>A real module exists in the field, but TurboSuite does not model it yet.</summary>
            NotYetSupported,

            /// <summary>A dedicated subsystem owns this protocol's control hardware and reports its own
            /// demand (DMX → TurboDMX). No DIN module, and no warning — the parts are counted, just not
            /// here.</summary>
            ExternalSubsystem
        }

        private readonly struct Entry
        {
            public Entry(Category category, string moduleKey = "")
            {
                Category = category;
                ModuleKey = moduleKey;
            }

            public Category Category { get; }

            /// <summary>The <see cref="Models.BrandConfig"/> module key; empty for non-module categories.</summary>
            public string ModuleKey { get; }
        }

        /// <summary>
        /// The protocol → module map. Adding or reclassifying a protocol is a one-line edit here
        /// (e.g. when the DALI module ships, DALI moves from NotYetSupported to Module).
        ///
        /// Module keys are the ones <see cref="Models.BrandConfig"/> defines for BOTH brands
        /// (Lutron and Crestron each declare ELV / 0-10V / Relay), so allocation, amp limits, and the
        /// panel-breakdown color converter keep working unchanged. If brands ever diverge on which
        /// module a protocol uses, this table moves into BrandConfig.
        ///
        /// Matching is case-insensitive and trimmed, but literal on hyphens and spacing — "0-10V"
        /// must be authored exactly.
        /// </summary>
        private static readonly Dictionary<string, Entry> Map =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
            {
                { "ELV",   new Entry(Category.Module, "ELV") },
                { "0-10V", new Entry(Category.Module, "0-10V") },
                { "MLV",   new Entry(Category.Module, "ELV") },   // not identity — MLV dims on an ELV module
                { "RELAY", new Entry(Category.Module, "Relay") }, // "switch a dimmable load", expressed as a type

                // Network-controlled: never rides a dimming module. Excluded silently, like a
                // switch-wired circuit — this is a design decision, not a missing parameter.
                { "WIFI",  new Entry(Category.NoModule) },

                // DMX rides no DIN module at all: the QSE-CI-DMX is a QS-link interface in the panel's
                // LV compartment, and TurboDMX — which knows the channel math — reports how many.
                // Silent here, because the parts are counted, just not by this map.
                { "DMX",   new Entry(Category.ExternalSubsystem) },

                // A real Lutron module TurboSuite does not allocate yet. Flagged rather than passed
                // through, so it cannot become a phantom BOM part.
                { "DALI",  new Entry(Category.NotYetSupported) }
            };

        /// <summary>
        /// Resolves the module type and display protocol for one circuit from its member
        /// fixtures' raw Dimming Protocol values.
        /// </summary>
        /// <param name="fixtureProtocols">
        /// Raw per-fixture values. Blank/whitespace entries are ignored (lenient, matching
        /// DriverSelectionService, which considers only declared protocols).
        /// </param>
        public static DimmingResolution Resolve(IEnumerable<string?>? fixtureProtocols)
        {
            // Dedupe case-insensitively but keep the first-seen spelling, then sort so the
            // display string — and the module key chosen below — do not depend on the order
            // Revit happened to enumerate the circuit's elements in.
            var declared = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (fixtureProtocols != null)
            {
                foreach (string? raw in fixtureProtocols)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string trimmed = raw!.Trim();
                    if (seen.Add(trimmed))
                        declared.Add(trimmed);
                }
            }
            declared.Sort(StringComparer.OrdinalIgnoreCase);

            if (declared.Count == 0)
                return new DimmingResolution(string.Empty, string.Empty, DimmingResolveOutcome.NoProtocol);

            string display = string.Join("; ", declared);

            // A module-category protocol wins outright: one circuit resolves to one module type,
            // and any co-present WIFI/DALI/unknown on the same circuit is ignored. This preserves
            // the pre-existing "one circuit = one module" behavior — which only gets rarer now that
            // authoring leans on a schedule-visible field.
            foreach (string protocol in declared)
            {
                if (Map.TryGetValue(protocol, out Entry entry) && entry.Category == Category.Module)
                    return new DimmingResolution(display, entry.ModuleKey, DimmingResolveOutcome.Allocatable);
            }

            // No module anywhere. If everything declared is deliberately module-less, that is a
            // legitimate configuration and stays silent; otherwise it needs a human's attention.
            // A benched protocol always gets flagged, even alongside something legitimate. Otherwise the
            // circuit stays silent only when EVERY declared value is accounted for — module-less by
            // design, or owned by a subsystem. One unrecognized value in the mix is still an authoring
            // gap, and being co-declared with WIFI or DMX must not hide it.
            bool anyNotYetSupported = false;
            bool anyExternal = false;
            bool allAccountedFor = true;
            foreach (string protocol in declared)
            {
                if (!Map.TryGetValue(protocol, out Entry entry)) { allAccountedFor = false; continue; }

                switch (entry.Category)
                {
                    case Category.NoModule: break;
                    case Category.ExternalSubsystem: anyExternal = true; break;
                    case Category.NotYetSupported: anyNotYetSupported = true; allAccountedFor = false; break;
                    default: allAccountedFor = false; break;
                }
            }

            if (anyNotYetSupported)
                return new DimmingResolution(display, string.Empty, DimmingResolveOutcome.NotYetSupported);

            if (allAccountedFor)
                return new DimmingResolution(display, string.Empty,
                    anyExternal ? DimmingResolveOutcome.HandledBySubsystem
                                : DimmingResolveOutcome.NoModuleByDesign);

            return new DimmingResolution(display, string.Empty, DimmingResolveOutcome.NoProtocol);
        }
    }

    /// <summary>The outcome of resolving one circuit's fixtures to a control-module type.</summary>
    public readonly struct DimmingResolution
    {
        public DimmingResolution(string protocolDisplay, string moduleType, DimmingResolveOutcome outcome)
        {
            ProtocolDisplay = protocolDisplay;
            ModuleType = moduleType;
            Outcome = outcome;
        }

        /// <summary>The circuit's distinct declared protocols, "; "-joined — what the Loads PDF
        /// "Dimming" column shows, and what identifies a benched circuit in the Unassigned list.
        /// This is the raw protocol (e.g. "MLV"), never the mapped module key.</summary>
        public string ProtocolDisplay { get; }

        /// <summary>The single <see cref="Models.BrandConfig"/> module key to allocate on.
        /// Empty unless <see cref="Outcome"/> is <see cref="DimmingResolveOutcome.Allocatable"/>.</summary>
        public string ModuleType { get; }

        public DimmingResolveOutcome Outcome { get; }
    }
}
