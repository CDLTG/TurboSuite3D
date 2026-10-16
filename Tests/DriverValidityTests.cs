using TurboSuite.Driver.Services;
using Xunit;

namespace TurboSuite.Tests.Driver
{
    /// <summary>
    /// Oracles for <see cref="DriverValidity"/> — the shared Power / Sub-Driver Power validity rule
    /// used by both the candidate-pool collector and the match-check diagnostic. A driver is valid
    /// only when Power is a positive whole multiple of Sub-Driver Power.
    /// </summary>
    public class DriverValidityTests
    {
        [Theory]
        [InlineData(300, 100, 3)]
        [InlineData(100, 100, 1)]
        [InlineData(1000, 250, 4)]
        [InlineData(60, 7.5, 8)]
        public void ValidWhenPowerIsWholeMultiple(double power, double sub, int expectedCount)
        {
            var r = DriverValidity.Evaluate(power, sub);
            Assert.True(r.IsValid);
            Assert.Equal(expectedCount, r.SubDriverCount);
            Assert.Null(r.Reason);
        }

        [Theory]
        [InlineData(300, 90)]   // not a whole multiple
        [InlineData(225, 100)]
        public void InvalidWhenNotWholeMultiple(double power, double sub)
        {
            var r = DriverValidity.Evaluate(power, sub);
            Assert.False(r.IsValid);
            Assert.Equal(0, r.SubDriverCount);
            Assert.Contains("whole multiple", r.Reason);
        }

        [Theory]
        [InlineData(0, 100)]
        [InlineData(-10, 100)]
        public void InvalidWhenPowerUnset(double power, double sub)
        {
            var r = DriverValidity.Evaluate(power, sub);
            Assert.False(r.IsValid);
            Assert.Contains("Power", r.Reason);
        }

        [Theory]
        [InlineData(300, 0)]
        [InlineData(300, -5)]
        public void InvalidWhenSubDriverPowerUnset(double power, double sub)
        {
            var r = DriverValidity.Evaluate(power, sub);
            Assert.False(r.IsValid);
            Assert.Contains("Sub-Driver Power", r.Reason);
        }
    }
}
