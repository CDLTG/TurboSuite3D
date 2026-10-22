using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TurboSuite.Shared.Helpers;
using TurboSuite.Wire.Constants;
using Xunit;

namespace TurboSuite.Tests
{
    /// <summary>
    /// Pins TurboWire's scaled length bundle (Core/Wire/Constants/WireGeometry.cs). At the 1/4" reference
    /// every member must equal its pre-scale-awareness constant to the bit — the literals below keep
    /// TurboWire's original form (<c>v / 12.0</c>, or bare feet), since <c>v * (1.0 / 12.0)</c> rounds
    /// differently for several of them. Every member must also scale linearly with K, so an unscaled
    /// length can't hide in the bundle.
    /// </summary>
    public class WireGeometryTests
    {
        // Name → the exact legacy value (WireConstants before scale awareness, plus the inline literals:
        // WireCommand's 3D switch 9" and EndTieEpsilon/MinEndGap, WireCreationService's 0.5" nudge,
        // ArcCalculator.IsOffAxis's default threshold).
        private static readonly (string Name, double Legacy)[] Golden =
        {
            ("BaselineDistance", 4.0),
            ("SplineOffsetAlongWall", 6.0 / 12.0),
            ("SplineOffsetNearWall", 1.5 / 12.0),
            ("SplineOffsetMidFromWall", 6.5 / 12.0),
            ("SplineConnectorOffset", 2.5 / 12.0),
            ("ReceptacleSplineConnectorOffset", 3.0 / 12.0),
            ("ReceptacleSplineOffsetNearWall", 2.0 / 12.0),
            ("ReceptacleSplineOffsetMidFromWall", 7.0 / 12.0),
            ("SwitchOffsetFromWall", 9.0 / 12.0),
            ("WallNudgeInitialOffset", 0.5 / 12.0),
            ("EndTieEpsilon", 0.5),
            ("MinEndGap", 0.25),
            ("OffAxisThreshold", 0.5),
        };

        public static IEnumerable<object[]> GoldenRows => Golden.Select(g => new object[] { g.Name, g.Legacy });

        private static IReadOnlyList<PropertyInfo> LengthProperties =>
            typeof(WireGeometry)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(double))
                .ToList();

        private static double Read(WireGeometry g, string name) =>
            (double)typeof(WireGeometry).GetProperty(name)!.GetValue(g)!;

        [Theory]
        [MemberData(nameof(GoldenRows))]
        public void Reference_IsBitIdenticalToLegacy(string name, double legacy)
        {
            var g = new WireGeometry(PlanScale.Reference);
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
            var reference = new WireGeometry(PlanScale.Reference);
            var scaled = new WireGeometry(scale);

            foreach (var p in LengthProperties)
            {
                double expected = (double)p.GetValue(reference)! * scale.K;
                double actual = (double)p.GetValue(scaled)!;
                Assert.True(actual == expected, $"{p.Name} at 1:{viewScale}: {actual:R} != {expected:R}");
            }
        }

        // The spline's along-wall offset is SplineOffsetAlongWall × (distance / BaselineDistance): both
        // carry K, so for a fixed real distance it comes out the same at every scale. That cancellation is
        // what makes a 1/8" spline a small copy of the 1/4" drawing (plan §3.2, §5.3).
        [Theory]
        [InlineData(96)]
        [InlineData(64)]
        public void SplineAlongWallOffset_IsScaleIndependentForAFixedDistance(int viewScale)
        {
            const double distance = 7.0;
            var reference = new WireGeometry(PlanScale.Reference);
            var scaled = new WireGeometry(PlanScale.FromViewScale(viewScale));

            double atRef = reference.SplineOffsetAlongWall * (distance / reference.BaselineDistance);
            double atScale = scaled.SplineOffsetAlongWall * (distance / scaled.BaselineDistance);
            Assert.Equal(atRef, atScale, 12);
        }
    }
}
