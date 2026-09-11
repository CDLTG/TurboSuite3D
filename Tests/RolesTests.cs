using TurboSuite.Shared.Constants;
using Xunit;

namespace TurboSuite.Tests.Shared
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle for Roles.Canonicalize (Core/Shared/Constants/Roles.cs) — the ONE pure seam in the
    //  name-decoupling migration. Every classify/find site reads a family's "TurboSuite Role"
    //  through here, so this is where authoring-forgiveness (trim/case) and the no-fallback contract
    //  (unknown/blank → "" → no special behavior) are pinned. The rest is Revit reads, checked in-app.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    public class RolesTests
    {
        /// <summary>A recognized value returns its exact canonical constant regardless of the
        /// case/whitespace the author typed — classify and finder roles alike.</summary>
        [Theory]
        [InlineData("Sconce", Roles.Sconce)]
        [InlineData("sconce", Roles.Sconce)]                 // case-insensitive
        [InlineData("  Keypad  ", Roles.Keypad)]             // trimmed
        [InlineData("FIREPLACEIGNITER", Roles.FireplaceIgniter)]
        [InlineData("driverplaceholder", Roles.DriverPlaceholder)]
        [InlineData("fixturetypetag", Roles.FixtureTypeTag)] // finder role, same rules
        [InlineData(" ElectricalSwitchlegTag ", Roles.ElectricalSwitchlegTag)]
        [InlineData("dmxwiremarkannotation", Roles.DmxWireMarkAnnotation)]
        public void KnownValue_ReturnsCanonicalSpelling(string raw, string expected)
            => Assert.Equal(expected, Roles.Canonicalize(raw));

        /// <summary>Blank in every form is "no role" — the default path, indistinguishable from
        /// an absent parameter (most families carry no role).</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankOrNull_ReturnsEmpty(string? raw)
            => Assert.Equal(string.Empty, Roles.Canonicalize(raw));

        /// <summary>Anything outside the vocabulary collapses to "" — no name-match fallback. A typo
        /// (Ignitor vs Igniter) or a stray word silently takes the default path, which is exactly why
        /// the pre-release audit spike exists to catch it before it ships.</summary>
        [Theory]
        [InlineData("Ignitor")]        // the spelling we explicitly did NOT choose
        [InlineData("Sconces")]        // near-miss
        [InlineData("Wall Sconce")]    // a family name, not a role
        [InlineData("gibberish")]
        public void UnrecognizedValue_ReturnsEmpty(string raw)
            => Assert.Equal(string.Empty, Roles.Canonicalize(raw));

        /// <summary>A finder role gets its human phrase for "not found" messages; anything without a
        /// label (classify roles, unknowns, null) falls back to the token itself, never throwing.</summary>
        [Theory]
        [InlineData(Roles.FixtureTypeTag, "Lighting Fixture (Type) tag")]
        [InlineData(Roles.ElectricalSwitchlegTag, "Electrical Fixture (Switchleg) tag")]
        [InlineData(Roles.Sconce, Roles.Sconce)]     // classify role: no label, falls back to token
        [InlineData("gibberish", "gibberish")]
        public void Label_ReturnsFinderPhraseOrTokenFallback(string role, string expected)
            => Assert.Equal(expected, Roles.Label(role));

        [Fact]
        public void Label_NullIsEmpty() => Assert.Equal(string.Empty, Roles.Label(null));
    }
}
