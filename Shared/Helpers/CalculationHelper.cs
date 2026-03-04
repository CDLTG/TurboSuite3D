#nullable disable
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Driver.Models;

namespace TurboSuite.Shared.Helpers
{
    /// <summary>
    /// Helper class for calculations and aggregations
    /// </summary>
    public static class CalculationHelper
    {
        /// <summary>
        /// Calculate total linear length from list of fixtures
        /// </summary>
        public static double CalculateTotalLinearLength(List<FixtureData> fixtures)
        {
            if (fixtures == null || fixtures.Count == 0)
                return 0.0;

            return fixtures.Sum(f => f.LinearLength);
        }
    }
}
