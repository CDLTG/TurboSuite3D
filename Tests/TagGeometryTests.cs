using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TurboSuite.Shared.Helpers;
using TurboSuite.Tag.Constants;
using Xunit;

namespace TurboSuite.Tests
{
    /// <summary>
    /// Pins TurboTag's scaled length bundle (Core/Tag/Constants/TagGeometry.cs). At the 1/4" reference
    /// every member must equal its pre-scale-awareness constant to the bit — the literals below keep
    /// TurboTag's original <c>v / 12.0</c> form, since <c>v * (1.0 / 12.0)</c> rounds differently for
    /// several of them. Every member must also scale linearly with K, so an unscaled length can't hide
    /// in the bundle.
    /// </summary>
    public class TagGeometryTests
    {
        // Name → the exact legacy value (TagConstants before scale awareness; LinearOffsetFeet was also
        // TurboDriver's private split-fixture copy, the same 5.0 / 12.0).
        private static readonly (string Name, double Legacy)[] Golden =
        {
            ("DefaultSymbolSizeFeet", 4.5 / 12.0),
            ("VerticalOffsetFeet", 4.25 / 12.0),
            ("OffsetMarginRightFeet", 2.35 / 12.0),
            ("OffsetMarginLeftFeet", 1.7 / 12.0),
            ("DefaultTagWidthShort", 7.75 / 12.0),
            ("DefaultTagWidthMedium", 11.15 / 12.0),
            ("LinearOffsetFeet", 5.0 / 12.0),
            ("KeypadOffsetFeet", 9.0 / 12.0),
        };

        public static IEnumerable<object[]> GoldenRows => Golden.Select(g => new object[] { g.Name, g.Legacy });

        private static IReadOnlyList<PropertyInfo> LengthProperties =>
            typeof(TagGeometry)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(double))
                .ToList();

        private static double Read(TagGeometry g, string name) =>
            (double)typeof(TagGeometry).GetProperty(name)!.GetValue(g)!;

        [Theory]
        [MemberData(nameof(GoldenRows))]
        public void Reference_IsBitIdenticalToLegacy(string name, double legacy)
        {
            var g = new TagGeometry(PlanScale.Reference);
            Assert.True(Read(g, name) == legacy, $"{name}: {Read(g, name):R} != legacy {legacy:R}");
        }

        [Fact]
        public void EveryLength_HasAGoldenRow()
        {
            var bundle = LengthProperties.Select(p => p.Name).OrderBy(n => n);
            var golden = Golden.Select(g => g.Name).OrderBy(n => n);
            Assert.Equal(golden, bundle);
        }

        [Theory]
        [InlineData(96)]   // 1/8"  — K = 2, exact
        [InlineData(64)]   // 3/16" — K = 4/3
        public void EveryLength_ScalesLinearlyWithK(int viewScale)
        {
            var scale = PlanScale.FromViewScale(viewScale);
            var reference = new TagGeometry(PlanScale.Reference);
            var scaled = new TagGeometry(scale);

            foreach (var p in LengthProperties)
            {
                double expected = (double)p.GetValue(reference)! * scale.K;
                double actual = (double)p.GetValue(scaled)!;
                Assert.True(actual == expected, $"{p.Name} at 1:{viewScale}: {actual:R} != {expected:R}");
            }
        }
    }
}
