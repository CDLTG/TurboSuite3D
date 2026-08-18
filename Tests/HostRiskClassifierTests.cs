using TurboSuite.Shared.Hosting;
using Xunit;

namespace TurboSuite.Tests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for HostRiskClassifier (Core/Host/HostRiskClassifier.cs) — the pure tier logic
    //  behind TurboSnoop's host report. The point of the tool is the tier split: a linked casework/
    //  stairs host is a churn/orphan risk a wall is not, and a link-hosted element whose host no longer
    //  resolves is likely already orphaned. These tests pin those boundaries so a refactor can't quietly
    //  collapse "churn risk" or "orphaned" back into "hosted, fine".
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    public class HostRiskClassifierTests
    {
        [Theory]
        [InlineData("Walls")]
        [InlineData("Ceilings")]
        [InlineData("Floors")]
        [InlineData("Roofs")]
        public void LinkedStructuralHost_IsStable(string category)
        {
            var (tier, _) = HostRiskClassifier.Classify(HostKind.LinkedElement, category);
            Assert.Equal(HostRiskTier.Stable, tier);
        }

        [Theory]
        [InlineData("Casework")]
        [InlineData("Furniture")]
        [InlineData("Generic Models")]
        [InlineData("Stairs")]
        [InlineData("Doors")]
        public void LinkedNonStructuralHost_IsChurnRisk(string category)
        {
            var (tier, note) = HostRiskClassifier.Classify(HostKind.LinkedElement, category);
            Assert.Equal(HostRiskTier.ChurnRisk, tier);
            Assert.Contains(category, note);
        }

        [Fact]
        public void StableCategoryMatch_IsCaseInsensitive()
        {
            var (tier, _) = HostRiskClassifier.Classify(HostKind.LinkedElement, "walls");
            Assert.Equal(HostRiskTier.Stable, tier);
        }

        [Fact]
        public void LinkedElement_WithNullCategory_IsChurnRisk()
        {
            // An unknown linked host category is treated as churn-prone, not silently "stable".
            var (tier, _) = HostRiskClassifier.Classify(HostKind.LinkedElement, null);
            Assert.Equal(HostRiskTier.ChurnRisk, tier);
        }

        [Fact]
        public void Unhosted_IsUnhostedTier()
        {
            var (tier, _) = HostRiskClassifier.Classify(HostKind.Unhosted, null);
            Assert.Equal(HostRiskTier.Unhosted, tier);
        }

        [Fact]
        public void LinkedUnresolved_IsOrphaned()
        {
            var (tier, _) = HostRiskClassifier.Classify(HostKind.LinkedUnresolved, null);
            Assert.Equal(HostRiskTier.Orphaned, tier);
        }

        [Fact]
        public void HostDocElement_IsIntentional()
        {
            // A track fixture on its track family: hosted to your own model element, never a warning.
            var (tier, note) = HostRiskClassifier.Classify(HostKind.HostDocElement, "Lighting Fixtures");
            Assert.Equal(HostRiskTier.HostDocIntentional, tier);
            Assert.Contains("Lighting Fixtures", note);
        }
    }
}
