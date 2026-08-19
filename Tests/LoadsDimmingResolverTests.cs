using TurboSuite.Docs.Services;
using Xunit;

namespace TurboSuite.Tests.Docs
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for LoadsDimmingResolver (Core/Docs/Services/LoadsDimmingResolver.cs).
    //
    //  This is the Load Schedule "Dimming" column, and it answers a PURCHASING question (which wall/
    //  panel device this load needs), NOT the panel-BOM module question DimmingModuleResolver answers.
    //  The two deliberately diverge on switched circuits — that divergence is what these tests pin.
    //
    //  The rule: RELAY dominates. A Switch-type wall device carries Dimming Protocol = "RELAY"; its
    //  Dimmer-type siblings are blank so the fixtures' own ELV/0-10V shows through. So a dimmable
    //  fixture wired to a switch reads "RELAY" (buy a switch), not its latent ELV (which would wrongly
    //  say "buy an ELV dimmer"). Without RELAY, the column is exactly DimmingModuleResolver's display.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    public class LoadsDimmingResolverTests
    {
        /// <summary>The bug this fixes: a dimmable fixture on a switch. The switch device injects
        /// RELAY, which overrides the fixtures' ELV/0-10V so the row reads "buy a switch".</summary>
        [Theory]
        [InlineData("ELV", "RELAY")]
        [InlineData("0-10V", "RELAY")]
        [InlineData("MLV", "RELAY")]
        public void Relay_DominatesDimmableFixtures(string fixtureProtocol, string switchProtocol)
        {
            Assert.Equal("RELAY", LoadsDimmingResolver.ResolveDisplay(new[] { fixtureProtocol, switchProtocol }));
        }

        /// <summary>RELAY matching is case-insensitive and trimmed, and the displayed token is always
        /// canonical upper-case "RELAY" regardless of how it was authored.</summary>
        [Theory]
        [InlineData("relay")]
        [InlineData(" RELAY ")]
        [InlineData("Relay")]
        public void Relay_MatchIsCaseInsensitiveAndCanonicalized(string authored)
        {
            Assert.Equal("RELAY", LoadsDimmingResolver.ResolveDisplay(new[] { "ELV", authored }));
        }

        /// <summary>A relay-authored fixture (no switch device) still reads RELAY.</summary>
        [Fact]
        public void Relay_FromFixtureAlone()
        {
            Assert.Equal("RELAY", LoadsDimmingResolver.ResolveDisplay(new[] { "RELAY" }));
        }

        /// <summary>No RELAY present → the column is exactly the fixtures' resolved protocol. This is
        /// the dimmed case (panel module OR a blank-authored Dimmer-type wall device): the electrician
        /// buys the matching dimmer.</summary>
        [Theory]
        [InlineData("ELV", "ELV")]
        [InlineData("0-10V", "0-10V")]
        [InlineData("MLV", "MLV")]      // display is the raw protocol, not the mapped module key
        public void NoRelay_ShowsFixtureProtocol(string fixtureProtocol, string expected)
        {
            Assert.Equal(expected, LoadsDimmingResolver.ResolveDisplay(new[] { fixtureProtocol }));
        }

        /// <summary>A blank Dimmer-type device contributes nothing (blanks are ignored), so the
        /// fixtures' protocol shows through unchanged.</summary>
        [Fact]
        public void BlankDimmerDevice_DoesNotSuppressFixtureProtocol()
        {
            Assert.Equal("ELV", LoadsDimmingResolver.ResolveDisplay(new[] { "ELV", "", "   " }));
        }

        /// <summary>Empty, all-blank, and null input all resolve to an empty display, same as the
        /// underlying resolver.</summary>
        [Fact]
        public void EmptyInput_IsBlank()
        {
            Assert.Equal(string.Empty, LoadsDimmingResolver.ResolveDisplay(new string?[0]));
            Assert.Equal(string.Empty, LoadsDimmingResolver.ResolveDisplay(new string?[] { "", "  " }));
            Assert.Equal(string.Empty, LoadsDimmingResolver.ResolveDisplay(null));
        }
    }
}
