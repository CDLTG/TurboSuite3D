using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for ZonesLabelResolver (Core/Zones/Services/ZonesLabelResolver.cs).
    //  Pure load-name label resolution: priority circuit comments > fixture comments > load class.
    //  Feeds the auto-generated load names.
    //
    //  For me (Claude): two things that look like bugs and aren't.
    //  1. On the COMMENT paths the strip is "cut at first '(' then TrimEnd" — NOT "remove only the
    //     parenthetical", so "Bath (GFCI) area" → "Bath" (trailing " area" is dropped too).
    //  2. The load-classification FALLBACK does not strip at all. It used to, back when the
    //     classification was repurposed to smuggle module type ("DOWNLIGHTS (ELV)"). TurboSuite
    //     now reads module type from Dimming Protocol (DimmingModuleResolver), so a parenthetical
    //     there is legitimate content. The asymmetry is deliberate.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    public class ZonesLabelResolverTests
    {
        [Theory]
        // circuit comments win, parenthetical (and everything after it) stripped
        [InlineData("Kitchen (dimmed)", "ignored", "ignored", "Kitchen", LabelSource.CircuitComments)]
        [InlineData("Bath (GFCI) area", "", "", "Bath", LabelSource.CircuitComments)]
        [InlineData("NoParens", "", "", "NoParens", LabelSource.CircuitComments)]
        // leading '(' → empties the label but source still reflects where it came from
        [InlineData("(all)", "", "", "", LabelSource.CircuitComments)]
        // fall to fixture comments when circuit comments blank/whitespace
        [InlineData("", "Living", "ignored", "Living", LabelSource.FixtureComments)]
        [InlineData("   ", "Living (x)", "", "Living", LabelSource.FixtureComments)]
        // fall to load classification when both comment fields blank — read STRAIGHT, no stripping.
        // The classification is no longer repurposed to carry module type (see DimmingModuleResolver),
        // so a parenthetical in it is real content, not a smuggled "(ELV)" to cut off.
        [InlineData("", "", "Receptacles", "Receptacles", LabelSource.Fallback)]
        [InlineData("", "", "LIGHTING", "LIGHTING", LabelSource.Fallback)]
        [InlineData("", "", "LIGHTING (EXTERIOR)", "LIGHTING (EXTERIOR)", LabelSource.Fallback)]
        // nothing available
        [InlineData("", "", "", "", LabelSource.None)]
        [InlineData("   ", "  ", "   ", "", LabelSource.None)]
        public void ResolveLabel(string circuitComments, string fixtureComments,
            string loadClassificationName, string expectedLabel, LabelSource expectedSource)
        {
            string label = ZonesLabelResolver.ResolveLabel(
                circuitComments, fixtureComments, loadClassificationName, out LabelSource source);

            Assert.Equal(expectedLabel, label);
            Assert.Equal(expectedSource, source);
        }
    }
}
