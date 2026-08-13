using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using Xunit;

namespace TurboSuite.Tests
{
    /// <summary>
    /// Oracle tests for the legacy raw-CSV Counts export (Core/Docs/Services/LegacyCountsCsvService.cs):
    /// feet-to-{ft}ft{in}in rendering (nearest inch, always-show-feet), Type Mark concatenation, and the
    /// two-column CSV shape. Formatting only — pins the reproduction of the old native-schedule + Excel ritual.
    /// </summary>
    public class LegacyCountsCsvTests
    {
        [Theory]
        // 443'-4" and 32'-0" from the spec examples.
        [InlineData(443.0 + 4.0 / 12.0, "443ft4in")]
        [InlineData(32.0, "32ft0in")]
        // Nearest-inch rounding, half away from zero.
        [InlineData(1.0 + 5.4 / 12.0, "1ft5in")]  // 1'-5.4" → 1'-5"
        [InlineData(1.0 + 5.6 / 12.0, "1ft6in")]  // 1'-5.6" → 1'-6"
        // Sub-foot always shows 0ft.
        [InlineData(8.0 / 12.0, "0ft8in")]
        // Inch carry to a whole foot.
        [InlineData(11.6 / 12.0, "1ft0in")]        // 11.6" → 12" → 1'-0"
        // Zero (and zero-rounding) length → empty, so the caller emits a bare Type Mark.
        [InlineData(0.0, "")]
        [InlineData(0.02, "")]                      // 0.24" rounds to 0
        public void FormatLength_RendersFeetAndInches(double feet, string expected)
        {
            Assert.Equal(expected, LegacyCountsCsvService.FormatLength(feet));
        }

        [Fact]
        public void FormatTypeMark_AppendsLengthOnlyWhenLinear()
        {
            var linear = new CountsFixtureModel { TypeMark = "TL", LinearLength = 443.0 + 4.0 / 12.0 };
            var plain = new CountsFixtureModel { TypeMark = "A2", LinearLength = 0.0 };

            Assert.Equal("TL-443ft4in", LegacyCountsCsvService.FormatTypeMark(linear));
            Assert.Equal("A2", LegacyCountsCsvService.FormatTypeMark(plain));
        }

        [Fact]
        public void BuildCsv_EmitsRowsWithoutHeader()
        {
            var fixtures = new[]
            {
                new CountsFixtureModel { TypeMark = "A2", Count = 24, LinearLength = 0.0 },
                new CountsFixtureModel { TypeMark = "B1", Count = 6, LinearLength = 0.0 },
                new CountsFixtureModel { TypeMark = "TL", Count = 1, LinearLength = 443.0 + 4.0 / 12.0 },
                new CountsFixtureModel { TypeMark = "TF", Count = 1, LinearLength = 32.0 },
            };

            string csv = LegacyCountsCsvService.BuildCsv(fixtures);

            Assert.Equal(
                "A2,24\r\n" +
                "B1,6\r\n" +
                "TL-443ft4in,1\r\n" +
                "TF-32ft0in,1\r\n",
                csv);
        }

        [Fact]
        public void BuildCsv_QuotesFieldsWithCommas()
        {
            var fixtures = new[]
            {
                new CountsFixtureModel { TypeMark = "A,2", Count = 3, LinearLength = 0.0 },
            };

            string csv = LegacyCountsCsvService.BuildCsv(fixtures);

            Assert.Equal("\"A,2\",3\r\n", csv);
        }
    }
}
