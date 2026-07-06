using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using Xunit;

namespace TurboSuite.Tests.Docs
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for the Counts Catalog Qty override grammar + math
    //  (Core/Docs/Services/CatalogQtyEvaluator.cs). Blank ⇒ Count; N ⇒ Count×N; 1/N ⇒ ceil(Count/N);
    //  N @type ⇒ fixed N; N @ft / N @in ⇒ stock-cut Length mode (padded ×1.05, @in normalized to feet).
    //
    //  For me (Claude): Parse turns a raw string into a CatalogQtyRule; Evaluate applies it to a count
    //  (+ linear length for Length mode). Errors are typed (CatalogQtyParseException) with hint messages
    //  for the common near-miss forms. Derivations inline; re-derive before "fixing" a broken assertion.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Parse: raw override string → (mode, value). Covers every accepted form and the
    /// canonicalizations (@in → feet).</summary>
    public class CatalogQtyParseTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Blank_IsDefault(string? raw)
        {
            var rule = CatalogQtyParser.Parse(raw);
            Assert.Equal(CatalogQtyMode.Default, rule.Mode);
        }

        [Theory]
        [InlineData("3", 3.0)]
        [InlineData("2.5", 2.5)]
        public void BareNumber_IsPerFixture(string raw, double value)
        {
            var rule = CatalogQtyParser.Parse(raw);
            Assert.Equal(CatalogQtyMode.PerFixture, rule.Mode);
            Assert.Equal(value, rule.Value, precision: 6);
        }

        [Fact]
        public void Ratio_IsRatioPerFixture()
        {
            var rule = CatalogQtyParser.Parse("1/4");
            Assert.Equal(CatalogQtyMode.RatioPerFixture, rule.Mode);
            Assert.Equal(4, rule.Value, precision: 6);
        }

        [Fact]
        public void AtType_IsFixedPerType()
        {
            var rule = CatalogQtyParser.Parse("5 @type");
            Assert.Equal(CatalogQtyMode.FixedPerType, rule.Mode);
            Assert.Equal(5, rule.Value, precision: 6);
        }

        [Fact]
        public void AtFt_IsLength_InFeet()
        {
            var rule = CatalogQtyParser.Parse("16 @ft");
            Assert.Equal(CatalogQtyMode.Length, rule.Mode);
            Assert.Equal(16, rule.Value, precision: 6);
        }

        [Fact]
        public void AtIn_IsLength_NormalizedToFeet()
        {
            // 192 in ÷ 12 = 16 ft — identical Value to "16 @ft".
            var rule = CatalogQtyParser.Parse("192 @in");
            Assert.Equal(CatalogQtyMode.Length, rule.Mode);
            Assert.Equal(16, rule.Value, precision: 6);
        }

        [Theory]
        [InlineData("2/3")]      // numerator must be literal 1
        [InlineData("1/0")]      // denominator must be > 0
        [InlineData("1/x")]      // denominator must be integer
        [InlineData("1/2/3")]    // ratio must be exactly 1/N
        [InlineData("0")]        // must be > 0
        [InlineData("-2")]       // must be > 0
        [InlineData("abc")]      // unrecognized
        [InlineData("5 type")]   // missing '@' before type
        [InlineData("5 @length")]// @length retired — must specify @ft/@in
        [InlineData("@type")]    // missing quantity before suffix
        public void RejectsBadInput(string raw)
            => Assert.Throws<CatalogQtyParseException>(() => CatalogQtyParser.Parse(raw));
    }

    /// <summary>Evaluate: apply a parsed rule to a fixture count (and linear length for Length mode).
    /// Rounding is ceil throughout — you never under-order stock.</summary>
    public class CatalogQtyEvaluateTests
    {
        [Fact]
        public void Default_ReturnsCount()
            => Assert.Equal(7, CatalogQtyRule.DefaultRule.Evaluate(count: 7, linearLength: 0));

        [Theory]
        [InlineData(3.0, 4, 12)]   // ceil(4 * 3) = 12
        [InlineData(2.5, 3, 8)]    // ceil(3 * 2.5) = ceil(7.5) = 8
        public void PerFixture_CountTimesValue_CeilingRounded(double value, int count, int expected)
            => Assert.Equal(expected, new CatalogQtyRule(CatalogQtyMode.PerFixture, value).Evaluate(count, 0));

        [Fact]
        public void RatioPerFixture_CeilCountOverValue()
            => Assert.Equal(3, new CatalogQtyRule(CatalogQtyMode.RatioPerFixture, 4).Evaluate(count: 10, linearLength: 0)); // ceil(10/4)=3

        [Fact]
        public void FixedPerType_IgnoresCount()
            => Assert.Equal(5, new CatalogQtyRule(CatalogQtyMode.FixedPerType, 5).Evaluate(count: 99, linearLength: 0));

        [Fact]
        public void Length_PadsThenDividesByStock()
        {
            // stock 16 ft, linear 100 ft → ceil(ceil(100*1.05)/16) = ceil(105/16) = ceil(6.5625) = 7.
            Assert.Equal(7, new CatalogQtyRule(CatalogQtyMode.Length, 16).Evaluate(count: 1, linearLength: 100));
        }

        [Fact]
        public void Length_ZeroStock_FallsBackToCount()
            => Assert.Equal(4, new CatalogQtyRule(CatalogQtyMode.Length, 0).Evaluate(count: 4, linearLength: 100));
    }

    /// <summary>Batch validator: parse errors surface per (Type, slot); the Length mode additionally
    /// requires a positive Linear Length on the instances (else the stock-cut math is meaningless).</summary>
    public class CatalogQtyValidatorTests
    {
        private static CountsFixtureModel Fx(string qty0, double linearLength)
        {
            var f = new CountsFixtureModel { TypeMark = "T1", LinearLength = linearLength };
            f.CatalogQtys[0] = qty0;
            return f;
        }

        [Fact]
        public void LengthMode_WithoutLinearLength_Rejected()
        {
            var ex = Assert.Throws<CatalogQtyValidationException>(
                () => CatalogQtyValidator.ValidateOrThrow(new[] { Fx("16 @ft", linearLength: 0) }));
            Assert.Contains("positive Linear Length", ex.Errors[0].Reason);
        }

        [Fact]
        public void ParseError_SurfacedPerSlot()
        {
            var ex = Assert.Throws<CatalogQtyValidationException>(
                () => CatalogQtyValidator.ValidateOrThrow(new[] { Fx("2/3", linearLength: 10) }));
            Assert.Equal(1, ex.Errors[0].Slot); // 1-based slot index
        }

        [Fact]
        public void ValidRules_Pass()
        {
            CatalogQtyValidator.ValidateOrThrow(new[]
            {
                Fx("3", linearLength: 0),
                Fx("16 @ft", linearLength: 100),
            });
        }
    }
}
