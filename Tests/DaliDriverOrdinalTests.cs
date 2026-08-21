using TurboSuite.Dali.Input;
using Xunit;

namespace TurboSuite.Tests.Dali
{
    /// <summary>
    /// Oracles for <see cref="DaliDriverOrdinal"/> — the within-circuit ordinal read off a driver's Switch ID
    /// suffix. The base is a non-unique placeholder in real models (often <c>"—"</c>), so only the trailing
    /// <c>a/b/c</c> matters, and the strip rule must mirror <c>DeploymentExecutor.StripSwitchIdSuffix</c>
    /// exactly (a lone trailing lowercase letter, not preceded by another lowercase letter).
    /// </summary>
    public class DaliDriverOrdinalTests
    {
        [Theory]
        [InlineData("—", 0)]        // single driver: bare placeholder base, no suffix
        [InlineData("—a", 0)]       // multi-driver column: a → 0
        [InlineData("—b", 1)]
        [InlineData("—c", 2)]
        [InlineData("X01a", 0)]     // a real base + suffix
        [InlineData("X01b", 1)]
        [InlineData("Xa", 0)]       // secondLast 'X' is not lowercase ⇒ 'a' is the suffix
        public void ParsesSuffixOrdinal(string switchId, int expected)
            => Assert.Equal(expected, DaliDriverOrdinal.FromSwitchId(switchId));

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("X01")]         // no lowercase suffix
        [InlineData("ABC")]         // uppercase is never a suffix
        [InlineData("Xab")]         // secondLast 'a' is lowercase ⇒ not treated as a suffix
        public void NoSuffix_IsZero(string? switchId)
            => Assert.Equal(0, DaliDriverOrdinal.FromSwitchId(switchId));
    }
}
