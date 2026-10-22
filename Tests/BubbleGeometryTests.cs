using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TurboSuite.Bubble.Constants;
using TurboSuite.Shared.Helpers;
using Xunit;

namespace TurboSuite.Tests
{
    /// <summary>
    /// Pins TurboBubble's scaled length bundle (Core/Bubble/Constants/BubbleGeometry.cs). At the 1/4"
    /// reference every member must equal its pre-scale-awareness constant to the bit — the literals
    /// below keep the original <c>v * InchesToFeet</c> form, since <c>v / 12.0</c> rounds differently for
    /// many of them. Every member must also scale linearly with K, so an unscaled length can't hide in
    /// the bundle.
    /// </summary>
    public class BubbleGeometryTests
    {
        private const double In = 1.0 / 12.0;

        // Name → the exact legacy value (BubbleConstants before scale awareness, plus the five inline
        // literals promoted out of the calculators and BubbleCommand).
        private static readonly (string Name, double Legacy)[] Golden =
        {
            ("DefaultSymbolSizeFt", 4.5 * In),
            ("MinSymbolWidthFt", 4.0 * In),
            ("TagWidth1CharFt", 6.25 * In),
            ("TagWidth2CharsFt", 7.75 * In),
            ("TagWidth3CharsFt", 11.15 * In),
            ("WireHorizontalOffsetFt", 0.613 * In),
            ("WireVerticalOffsetFt", 0.65 * In),
            ("WireToSymbolGapFt", 4.215 * In),
            ("WireToSymbolGapHorizontalFt", 4.55 * In),
            ("WireToSymbolGapWallSconceFt", 0.5 * In),
            ("WireElbowOffsetFt", 3.56 * In),
            ("WireOffsetEndInitialFt", 0.5 * In),
            ("WireOffsetEndFinalFt", 1.0 * In),
            ("WireOffsetEndWallSconceFt", 2.5 * In),
            ("WallTagAlongWallGapFt", 8.25 * In),          // was inline in VerticalFacePlacementCalculator
            ("WallTagOffWallFt", 5.5 * In),                // was inline in VerticalFacePlacementCalculator
            ("LineBasedTagAlongFixtureFt", 14.0 * In),     // was inline in LineBasedPlacementCalculator
            ("LineBasedTagPerpendicularFt", 6.25 * In),    // was inline in LineBasedPlacementCalculator
            ("ElectricalVerticalTagOffsetFt", 10.0 * In),
            ("ElectricalVerticalArcRadiusFt", 4.5 * In),
            ("ElectricalVerticalArcCenterXFt", 3.78125 * In),
            ("ElectricalVerticalArcCenterYFt", 3.75 * In),
            ("ElectricalVerticalArcStartXFt", 4.0 * In),   // was inline twice in BubbleCommand
            ("ElectricalMidpointOffsetFt", 6.0 * In),
            ("ElectricalTagOffsetFt", 12.5 * In),
            ("ElectricalV2XOffsetFt", 3.47 * In),
            ("ElectricalV2YOffsetFt", 1.56 * In),
            ("ElectricalV3XOffsetFt", 6.9 * In),
            ("ElectricalV3YOffsetFt", 3.09 * In),
            ("ElectricalWireStartOffsetFt", 3.0 * In),
            ("CeilingFanTagXOffsetFt", 15.0 * In),
            ("CeilingFanTagYOffsetFt", 7.625 * In),
            ("CeilingFanV2XOffsetFt", 9.0625 * In),
            ("CeilingFanV2YOffsetFt", 1.75 * In),
            ("ChandelierBubbleOffsetExtensionFt", 8.0 * In),
            ("ChandelierBubbleHorizontalNudgeFt", 0.75 * In),
            ("ChandelierWireV2GapFt", 1.59 * In),
            ("ChandelierWireV2PerpKickFt", 3.50 * In),
            ("ChandelierWireElbowOffsetFt", 3.56 * In),
            ("PictureLightTagAlongWallGapFt", 8.75 * In),
            ("PictureLightTagClearanceFt", 4.25 * In),
            ("PictureLightWireEndInsetFt", 0.787 * In),
            ("PictureLightWireMidInsetFt", 1.587 * In),
            ("TagOffsetVerticalFt", 5.25 * In),
            ("TagOffsetHorizontalFt", 8.75 * In),
            ("TagXOffsetFt", 9.0 * In),
            ("RemoteSwitchlegExtraXOffsetFt", 5.15625 * In),
            ("LinearFeedDetailPerpOffsetFt", 1.0 * In),
            ("LinearFeedDetailLengthFt", 12.0 * In),
            ("LinearFeedTagParallelGapFt", 6.5 * In),
            ("LinearFeedTagPerpOffsetFt", 5.0 * In),
        };

        public static IEnumerable<object[]> GoldenRows => Golden.Select(g => new object[] { g.Name, g.Legacy });

        private static IReadOnlyList<PropertyInfo> LengthProperties =>
            typeof(BubbleGeometry)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(double))
                .ToList();

        private static double Read(BubbleGeometry g, string name) =>
            (double)typeof(BubbleGeometry).GetProperty(name)!.GetValue(g)!;

        [Theory]
        [MemberData(nameof(GoldenRows))]
        public void Reference_IsBitIdenticalToLegacy(string name, double legacy)
        {
            var g = new BubbleGeometry(PlanScale.Reference);
            Assert.True(Read(g, name) == legacy, $"{name}: {Read(g, name):R} != legacy {legacy:R}");
        }

        [Fact]
        public void EveryLength_HasAGoldenRow()
        {
            var bundle = LengthProperties.Select(p => p.Name).OrderBy(n => n);
            var golden = Golden.Select(g => g.Name).OrderBy(n => n);
            Assert.Equal(golden, bundle);
        }

        // The ceiling-fan wire start is anchored to the fan base, which is drawn at true size, so it is
        // a real-world constant outside the bundle — pinned here to its legacy value all the same.
        [Fact]
        public void CeilingFanWireStart_IsUnscaledLegacy()
        {
            Assert.True(BubbleConstants.CeilingFanV1XOffsetFt == 4.1875 * In);
            Assert.True(BubbleConstants.CeilingFanV1YOffsetFt == 1.78125 * In);
            Assert.Null(typeof(BubbleGeometry).GetProperty("CeilingFanV1XOffsetFt"));
            Assert.Null(typeof(BubbleGeometry).GetProperty("CeilingFanV1YOffsetFt"));
        }

        [Theory]
        [InlineData(96)]   // 1/8"  — K = 2, exact
        [InlineData(64)]   // 3/16" — K = 4/3
        public void EveryLength_ScalesLinearlyWithK(int viewScale)
        {
            var scale = PlanScale.FromViewScale(viewScale);
            var reference = new BubbleGeometry(PlanScale.Reference);
            var scaled = new BubbleGeometry(scale);

            foreach (var p in LengthProperties)
            {
                double expected = (double)p.GetValue(reference)! * scale.K;
                double actual = (double)p.GetValue(scaled)!;
                Assert.True(actual == expected, $"{p.Name} at 1:{viewScale}: {actual:R} != {expected:R}");
            }
        }
    }
}
