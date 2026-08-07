using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for DimmingModuleResolver (Core/Zones/Services/DimmingModuleResolver.cs).
    //  Maps a circuit's fixtures' "Dimming Protocol" to the control-module key panel allocation
    //  runs on. This replaced the connector-level "Load Classification Abbreviation" — an invisible
    //  field that silently dropped circuits out of the panel BOM when unauthored.
    //
    //  For me (Claude): the four categories are NOT interchangeable and the distinction is the
    //  whole point. WIFI is NoModuleByDesign (silent — it legitimately rides no module); DMX is
    //  HandledBySubsystem (also silent, but for a different reason — TurboDMX counts the QSE-CI-DMX
    //  interfaces, so the hardware IS ordered, just not from this map); DALI is NotYetSupported
    //  (loud — a real module TurboSuite doesn't allocate yet). A test asserting WIFI or DMX shows up
    //  in the Unassigned list is asserting the bug this design prevents.
    //
    //  MLV → ELV is the entry that proves the map is needed: module type is a function of protocol,
    //  not the identity of it. Don't "simplify" the map away.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    public class DimmingModuleResolverTests
    {
        /// <summary>Module-category protocols resolve to their BrandConfig key. Matching is
        /// case-insensitive and trimmed; the returned key keeps canonical casing regardless.</summary>
        [Theory]
        [InlineData("ELV", "ELV")]
        [InlineData("0-10V", "0-10V")]
        [InlineData("MLV", "ELV")]      // not identity — MLV dims on an ELV module
        [InlineData("RELAY", "Relay")]
        [InlineData("relay", "Relay")]  // case-insensitive
        [InlineData(" ELV ", "ELV")]    // trimmed
        [InlineData("mlv", "ELV")]
        public void ModuleProtocols_Allocatable(string protocol, string expectedKey)
        {
            var r = DimmingModuleResolver.Resolve(new[] { protocol });

            Assert.Equal(DimmingResolveOutcome.Allocatable, r.Outcome);
            Assert.Equal(expectedKey, r.ModuleType);
        }

        /// <summary>The non-allocating categories, which differ only in how loudly
        /// they're surfaced downstream.</summary>
        [Theory]
        [InlineData("WIFI", DimmingResolveOutcome.NoModuleByDesign)]
        [InlineData("wifi", DimmingResolveOutcome.NoModuleByDesign)]
        [InlineData("DALI", DimmingResolveOutcome.NotYetSupported)]
        [InlineData("DMX", DimmingResolveOutcome.HandledBySubsystem)]
        [InlineData("dmx", DimmingResolveOutcome.HandledBySubsystem)]
        [InlineData("PHASE-CUT", DimmingResolveOutcome.NoProtocol)] // off-vocabulary → authoring gap
        public void NonModuleProtocols_CategorizedByOutcome(string protocol, DimmingResolveOutcome expected)
        {
            var r = DimmingModuleResolver.Resolve(new[] { protocol });

            Assert.Equal(expected, r.Outcome);
            Assert.Equal(string.Empty, r.ModuleType);
        }

        /// <summary>Nothing declared at all — the blank-authoring case.</summary>
        [Fact]
        public void NoDeclaredProtocol_NoProtocol()
        {
            AssertNothingDeclared(new string?[0]);           // circuit with no fixtures
            AssertNothingDeclared(new string?[] { "" });     // fixture with the parameter empty
            AssertNothingDeclared(new string?[] { "   ", null });

            static void AssertNothingDeclared(string?[] protocols)
            {
                var r = DimmingModuleResolver.Resolve(protocols);

                Assert.Equal(DimmingResolveOutcome.NoProtocol, r.Outcome);
                Assert.Equal(string.Empty, r.ModuleType);
                Assert.Equal(string.Empty, r.ProtocolDisplay);
            }
        }

        [Fact]
        public void Null_TreatedAsNothingDeclared()
            => Assert.Equal(DimmingResolveOutcome.NoProtocol,
                DimmingModuleResolver.Resolve(null).Outcome);

        /// <summary>A circuit whose fixtures disagree still resolves to ONE module — the
        /// pre-existing "one circuit = one module type" invariant, preserved deliberately.</summary>
        [Fact]
        public void MixedModuleProtocols_ResolveToOneKey_DisplayShowsBoth()
        {
            var r = DimmingModuleResolver.Resolve(new[] { "MLV", "ELV" });

            Assert.Equal(DimmingResolveOutcome.Allocatable, r.Outcome);
            Assert.Equal("ELV", r.ModuleType);          // both map to ELV anyway
            Assert.Equal("ELV; MLV", r.ProtocolDisplay); // sorted, so display is enumeration-order-proof
        }

        [Fact]
        public void MixedModuleProtocols_DifferentKeys_TakeFirstInSortedOrder()
        {
            // "0-10V" sorts before "ELV", so the key is deterministic no matter which order
            // Revit handed back the circuit's elements.
            var forward = DimmingModuleResolver.Resolve(new[] { "ELV", "0-10V" });
            var reverse = DimmingModuleResolver.Resolve(new[] { "0-10V", "ELV" });

            Assert.Equal("0-10V", forward.ModuleType);
            Assert.Equal(forward.ModuleType, reverse.ModuleType);
            Assert.Equal("0-10V; ELV", forward.ProtocolDisplay);
            Assert.Equal(forward.ProtocolDisplay, reverse.ProtocolDisplay);
        }

        /// <summary>Blank entries are ignored rather than poisoning the circuit — lenient,
        /// matching DriverSelectionService, which considers only declared protocols.</summary>
        [Fact]
        public void BlankFixtureProtocol_IgnoredNotFatal()
        {
            var r = DimmingModuleResolver.Resolve(new[] { "ELV", "", "  " });

            Assert.Equal(DimmingResolveOutcome.Allocatable, r.Outcome);
            Assert.Equal("ELV", r.ModuleType);
            Assert.Equal("ELV", r.ProtocolDisplay);
        }

        /// <summary>A module protocol wins outright: co-present non-module protocols on the same
        /// circuit don't downgrade it. A WIFI fixture sharing a circuit with an ELV one still
        /// leaves an ELV circuit to allocate.</summary>
        [Theory]
        [InlineData("WIFI")]
        [InlineData("DALI")]
        [InlineData("GIBBERISH")]
        public void ModuleProtocol_WinsOverCoPresentNonModule(string other)
        {
            var r = DimmingModuleResolver.Resolve(new[] { "ELV", other });

            Assert.Equal(DimmingResolveOutcome.Allocatable, r.Outcome);
            Assert.Equal("ELV", r.ModuleType);
        }

        /// <summary>Among non-module protocols, NotYetSupported outranks Unknown (there's a real
        /// module to point at), and all-NoModule stays silent even when duplicated.</summary>
        [Fact]
        public void NotYetSupported_OutranksUnknown()
            => Assert.Equal(DimmingResolveOutcome.NotYetSupported,
                DimmingModuleResolver.Resolve(new[] { "GIBBERISH", "DALI" }).Outcome);

        [Fact]
        public void WifiMixedWithUnsupported_IsNotSilent()
        {
            // WIFI alone is silent, but paired with DALI the circuit still has an unmodeled
            // module in it — silence here would hide a real gap.
            var r = DimmingModuleResolver.Resolve(new[] { "WIFI", "DALI" });
            Assert.Equal(DimmingResolveOutcome.NotYetSupported, r.Outcome);
        }

        [Fact]
        public void AllWifi_StaysSilent()
            => Assert.Equal(DimmingResolveOutcome.NoModuleByDesign,
                DimmingModuleResolver.Resolve(new[] { "WIFI", "wifi", " WIFI " }).Outcome);

        /// <summary>DMX co-declared with WIFI stays silent AND stays DMX-flavored: both are accounted
        /// for, and the subsystem outcome is the more informative of the two — it is what tells the
        /// allocator a real interface is being counted elsewhere.</summary>
        [Fact]
        public void DmxMixedWithWifi_IsHandledBySubsystem()
            => Assert.Equal(DimmingResolveOutcome.HandledBySubsystem,
                DimmingModuleResolver.Resolve(new[] { "WIFI", "DMX" }).Outcome);

        /// <summary>The owning subsystem is named, in the map's canonical casing rather than however
        /// the family author typed it — the allocator matches on it to ask whether that subsystem
        /// actually accounted for the circuit.</summary>
        [Fact]
        public void HandledBySubsystem_NamesItsSubsystemCanonically()
            => Assert.Equal("DMX", DimmingModuleResolver.Resolve(new[] { " dmx " }).Subsystem);

        /// <summary>Every other outcome leaves it empty — nothing owns those circuits.</summary>
        [Theory]
        [InlineData("ELV")]
        [InlineData("WIFI")]
        [InlineData("DALI")]
        [InlineData("GIBBERISH")]
        public void NonSubsystemProtocols_NameNoSubsystem(string protocol)
            => Assert.Equal(string.Empty, DimmingModuleResolver.Resolve(new[] { protocol }).Subsystem);

        /// <summary>But DMX must not launder an authoring gap. A subsystem owning one declared value
        /// says nothing about an unrecognized one sitting next to it — same rule WIFI follows.</summary>
        [Fact]
        public void DmxMixedWithUnknown_IsNotSilent()
            => Assert.Equal(DimmingResolveOutcome.NoProtocol,
                DimmingModuleResolver.Resolve(new[] { "DMX", "GIBBERISH" }).Outcome);

        /// <summary>DALI outranks DMX: the circuit still has a module nobody is counting, and the
        /// subsystem's silence must not swallow it.</summary>
        [Fact]
        public void DaliMixedWithDmx_StaysFlagged()
            => Assert.Equal(DimmingResolveOutcome.NotYetSupported,
                DimmingModuleResolver.Resolve(new[] { "DMX", "DALI" }).Outcome);

        /// <summary>Display dedupes case-insensitively but keeps the first-seen spelling.</summary>
        [Fact]
        public void ProtocolDisplay_DedupesCaseInsensitively()
            => Assert.Equal("ELV", DimmingModuleResolver.Resolve(new[] { "ELV", "elv", "ELV" }).ProtocolDisplay);

        /// <summary>The display is the RAW protocol, never the module key it maps to — the Loads
        /// PDF must print what a reader would find on the fixture.</summary>
        [Fact]
        public void ProtocolDisplay_IsRawProtocolNotModuleKey()
        {
            var r = DimmingModuleResolver.Resolve(new[] { "MLV" });

            Assert.Equal("MLV", r.ProtocolDisplay);
            Assert.Equal("ELV", r.ModuleType);
        }
    }
}
