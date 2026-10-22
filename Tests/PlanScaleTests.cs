using TurboSuite.Shared.Helpers;
using Xunit;

namespace TurboSuite.Tests
{
    /// <summary>
    /// Oracle tests for the plan-scale factor (Core/Shared/Helpers/PlanScale.cs): K = view scale ÷ 48,
    /// exactly 1.0 at the 1/4" reference (the byte-identity guarantee), and never zero.
    /// </summary>
    public class PlanScaleTests
    {
        [Fact]
        public void QuarterInch_IsExactlyOne()
        {
            Assert.Equal(48, PlanScale.ReferenceScale);
            Assert.True(PlanScale.FromViewScale(48).K == 1.0);
            Assert.True(PlanScale.Reference.K == 1.0);
        }

        [Theory]
        [InlineData(96, 2.0)]          // 1/8"
        [InlineData(64, 4.0 / 3.0)]    // 3/16"
        [InlineData(24, 0.5)]          // 1/2"
        [InlineData(192, 4.0)]         // 1/16"
        public void OtherScales_AreViewScaleOverReference(int viewScale, double expectedK)
        {
            Assert.True(PlanScale.FromViewScale(viewScale).K == expectedK);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-48)]
        public void NonPositiveScale_FallsBackToReference(int viewScale)
        {
            Assert.Same(PlanScale.Reference, PlanScale.FromViewScale(viewScale));
        }
    }
}
