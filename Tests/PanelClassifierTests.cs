using TurboSuite.Shared.Services;
using Xunit;

namespace TurboSuite.Tests
{
    /// <summary>
    /// Oracle tests for <see cref="PanelClassifier"/> (Core/Shared/Services/PanelClassifier.cs): a panel is
    /// a lighting panel unless its distribution system is exactly the 35 V shade/control system. Fails open —
    /// a null/empty/unknown distribution system is a lighting panel (never hidden from the picker) but is NOT
    /// a shade panel (never grabbed by a shade-only picker). Ground truth from the in-model TurboSpike probe:
    /// blank-named shade panels report "35 V", the lighting control panel reports "120 V".
    /// </summary>
    public class PanelClassifierTests
    {
        [Theory]
        [InlineData("35 V")]
        [InlineData("35 v")]      // case-insensitive
        [InlineData("  35 V  ")]  // trimmed
        public void ShadeDistributionSystem_IsShade_NotLighting(string dist)
        {
            Assert.True(PanelClassifier.IsShadePanel(dist));
            Assert.False(PanelClassifier.IsLightingPanel(dist));
        }

        [Theory]
        [InlineData("120 V")]
        [InlineData("277 V")]
        [InlineData("120/208 Wye")]
        public void LightingDistributionSystem_IsLighting_NotShade(string dist)
        {
            Assert.True(PanelClassifier.IsLightingPanel(dist));
            Assert.False(PanelClassifier.IsShadePanel(dist));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void UnreadableDistribution_FailsOpen_LightingButNotShade(string? dist)
        {
            Assert.True(PanelClassifier.IsLightingPanel(dist));   // never hide a real panel
            Assert.False(PanelClassifier.IsShadePanel(dist));     // never grab an unknown as a shade panel
        }

        [Fact]
        public void SimilarButNotExact_35V_IsNotShade()
        {
            // Only the exact "35 V" system is shade; a 35 V-ish lighting system stays a lighting panel.
            Assert.True(PanelClassifier.IsLightingPanel("35 V Lighting"));
            Assert.False(PanelClassifier.IsShadePanel("35 V Lighting"));
        }
    }
}
