using System.Collections.Generic;
using System.Linq;
using TurboSuite.Abstractions;
using TurboSuite.Driver.Models;
using TurboSuite.Driver.Services;
using Xunit;

namespace TurboSuite.Tests.Driver
{
    /// <summary>
    /// Oracles for driver-family ties and fixture-type → driver-family links:
    /// <list type="bullet">
    /// <item>A <b>tie</b> is an unlinked circuit whose best real candidate is equalled on every ranking
    /// criterion by a candidate from a <i>different</i> family. TBD never ties; same-family equals are
    /// not ties.</item>
    /// <item>A <b>link</b> restricts the pool to one family (hard — no fallback to other families or
    /// TBD); conflicting links fail; a linked fixture governs unlinked ones on the same circuit.</item>
    /// </list>
    /// Plus the classifier's TIE precedence, the deferral signature's tie suffix, and the Driver Match
    /// family comparison.
    /// </summary>
    public class DriverLinkTieTests
    {
        private const long FamA = 10, FamB = 20, FamC = 30, FamTbd = 90, FamTbd2 = 91;
        private const long TypeL1 = 100, TypeL2 = 101;

        private static DriverCandidateInfo Drv(long id, string family, long familyRef,
            double power = 60, double sub = 60, string mfr = "ACME", string volts = "24",
            string protocol = "0-10V", double derate = 1.0, int maxFixtures = 0, bool tbd = false,
            string? catalog = null)
            => new DriverCandidateInfo
            {
                SymbolRef = new ElementRef(id),
                FamilyTypeName = $"{family}-{power:0}W",
                FamilyName = family,
                FamilyRef = new ElementRef(familyRef),
                CatalogNumber = catalog ?? $"{family}-{power:0}",
                Manufacturer = mfr,
                TotalPower = power,
                SubDriverPower = sub,
                SubDriverCount = sub > 0 ? (int)(power / sub) : 0,
                IsValidDriver = true,
                DimmingProtocol = tbd ? null : protocol,
                Voltage = tbd ? null : volts,
                DerateFactor = derate,
                MaximumFixtures = maxFixtures,
                IsTbd = tbd
            };

        private static FixtureData Fix(long id, long typeRef = TypeL1, double watts = 30,
            string mfr = "ACME", string? volts = "24", string? protocol = "0-10V", string mark = "L1")
            => new FixtureData
            {
                FixtureId = new ElementRef(id),
                TypeRef = new ElementRef(typeRef),
                TypeDisplayName = $"Fixture : {mark}",
                TypeMark = mark,
                TypePower = watts,
                Manufacturer = mfr,
                DimmingProtocol = protocol,
                Voltage = volts,
                HasRemotePowerSupply = true
            };

        private static DriverLinks Links(params (long type, long family)[] pairs)
            => DriverLinks.From(pairs.ToDictionary(p => new ElementRef(p.type), p => new ElementRef(p.family)));

        private static DriverRecommendation Recommend(List<FixtureData> fixtures,
            List<DriverCandidateInfo> candidates, DriverLinks? links = null)
            => new DriverSelectionService().GetRecommendation(fixtures, candidates, links);

        // ── Tie detection ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void TwoFamilies_IdenticalSpecs_Tie_WinnerFirst()
        {
            var a = Drv(1, "A", FamA);
            var b = Drv(2, "B", FamB);
            var rec = Recommend(new List<FixtureData> { Fix(1) }, new List<DriverCandidateInfo> { a, b });

            Assert.True(rec.HasMatch);
            Assert.True(rec.IsTie);
            Assert.Equal(new[] { "A", "B" }, rec.TiedCandidates.Select(c => c.FamilyName));
            Assert.Same(a, rec.RecommendedCandidate);
            Assert.Null(rec.LinkedFamilyName);
        }

        [Fact]
        public void ThreeFamilies_OneRepresentativePerFamily()
        {
            var cands = new List<DriverCandidateInfo>
            {
                Drv(1, "A", FamA), Drv(2, "A", FamA, catalog: "A-dup"),
                Drv(3, "B", FamB), Drv(4, "C", FamC)
            };
            var rec = Recommend(new List<FixtureData> { Fix(1) }, cands);

            Assert.Equal(new[] { "A", "B", "C" }, rec.TiedCandidates.Select(c => c.FamilyName));
        }

        [Fact]
        public void DifferentManufacturerOutcome_NoTie()
        {
            var rec = Recommend(new List<FixtureData> { Fix(1) },
                new List<DriverCandidateInfo> { Drv(1, "A", FamA), Drv(2, "B", FamB, mfr: "OTHER") });

            Assert.False(rec.IsTie);
            Assert.Empty(rec.TiedCandidates);
            Assert.Equal("A", rec.RecommendedCandidate.FamilyName);
        }

        [Fact]
        public void DerateAndMaxFixturesDiffer_SameDriverCount_StillTie()
        {
            // 4 × 7.5 W = 30 W fits one 100 W unit for both, derated (80 W) or not, and B's
            // 10-fixture limit never binds — so nothing the ranking sees differs.
            var fixtures = Enumerable.Range(0, 4).Select(i => Fix(i, watts: 7.5)).ToList();
            var rec = Recommend(fixtures, new List<DriverCandidateInfo>
            {
                Drv(1, "A", FamA, power: 100, sub: 100),
                Drv(2, "B", FamB, power: 100, sub: 100, derate: 0.8, maxFixtures: 10)
            });

            Assert.True(rec.IsTie);
        }

        [Fact]
        public void HeavierLoad_DerateChangesCount_NoTie()
        {
            // 90 W: A holds it in one 100 W unit; B's derated 80 W ceiling splits it across two.
            var rec = Recommend(new List<FixtureData> { Fix(1, watts: 90) }, new List<DriverCandidateInfo>
            {
                Drv(1, "A", FamA, power: 100, sub: 100),
                Drv(2, "B", FamB, power: 100, sub: 100, derate: 0.8)
            });

            Assert.False(rec.IsTie);
            Assert.Equal("A", rec.RecommendedCandidate.FamilyName);
            Assert.Equal(1, rec.DriverCount);
        }

        [Fact]
        public void SameFamily_IdenticalTypes_NoTie()
        {
            var rec = Recommend(new List<FixtureData> { Fix(1) },
                new List<DriverCandidateInfo> { Drv(1, "A", FamA), Drv(2, "A", FamA) });

            Assert.False(rec.IsTie);
            Assert.Equal(1L, rec.RecommendedCandidate.SymbolRef.Value);   // first by order, as today
        }

        [Fact]
        public void TbdOnly_NoTie()
        {
            var rec = Recommend(new List<FixtureData> { Fix(1) },
                new List<DriverCandidateInfo> { Drv(9, "TBD", FamTbd, tbd: true) });

            Assert.True(rec.HasMatch);
            Assert.False(rec.IsTie);
        }

        [Fact]
        public void TwoTbdFamilies_NoTie()
        {
            // Underspecified fixture (no Voltage) ⇒ no real driver qualifies, TBD wins.
            var rec = Recommend(new List<FixtureData> { Fix(1, volts: null) }, new List<DriverCandidateInfo>
            {
                Drv(1, "A", FamA),
                Drv(8, "TBD", FamTbd, tbd: true),
                Drv(9, "TBD2", FamTbd2, tbd: true)
            });

            Assert.True(rec.RecommendedCandidate.IsTbd);
            Assert.False(rec.IsTie);
        }

        [Fact]
        public void RealPlusTbd_NoTie()
        {
            var rec = Recommend(new List<FixtureData> { Fix(1) },
                new List<DriverCandidateInfo> { Drv(1, "A", FamA), Drv(9, "TBD", FamTbd, tbd: true) });

            Assert.False(rec.IsTie);
            Assert.Equal("A", rec.RecommendedCandidate.FamilyName);
        }

        [Fact]
        public void NoLinks_ExistingLadderUnchanged_NoTie()
        {
            // The nine-type single-family transformer ladder (no FamilyRef) never reports a tie.
            var ladder = new[] { 100.0, 150, 250 }
                .Select(r => new DriverCandidateInfo
                {
                    SymbolRef = new ElementRef((long)r), FamilyTypeName = $"{r:0}W", FamilyName = "XFMR",
                    Manufacturer = "ACME", TotalPower = r, SubDriverPower = r, SubDriverCount = 1,
                    IsValidDriver = true, DimmingProtocol = "0-10V", Voltage = "24"
                }).ToList();
            var rec = Recommend(new List<FixtureData> { Fix(1) }, ladder);

            Assert.Equal("100W", rec.DriverType);
            Assert.False(rec.IsTie);
        }

        // ── Links ────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Link_RestrictsToLinkedFamily_NoTie()
        {
            var rec = Recommend(new List<FixtureData> { Fix(1) },
                new List<DriverCandidateInfo> { Drv(1, "A", FamA), Drv(2, "B", FamB) },
                Links((TypeL1, FamB)));

            Assert.True(rec.HasMatch);
            Assert.False(rec.IsTie);
            Assert.Equal("B", rec.RecommendedCandidate.FamilyName);
            Assert.Equal("B", rec.LinkedFamilyName);
        }

        [Fact]
        public void Link_StillSizesWithinFamily()
        {
            var rec = Recommend(new List<FixtureData> { Fix(1, watts: 100) }, new List<DriverCandidateInfo>
            {
                Drv(1, "A", FamA, power: 120, sub: 120),
                Drv(2, "B", FamB, power: 60, sub: 60),
                Drv(3, "B", FamB, power: 120, sub: 120)
            }, Links((TypeL1, FamB)));

            Assert.Equal(3L, rec.RecommendedCandidate.SymbolRef.Value);
            Assert.Equal(1, rec.DriverCount);
        }

        [Fact]
        public void LinkedFamily_GatedOut_NoMatch_NamesFamily_NoTbdFallback()
        {
            var rec = Recommend(new List<FixtureData> { Fix(1) }, new List<DriverCandidateInfo>
            {
                Drv(1, "A", FamA),
                Drv(2, "B", FamB, protocol: "DALI"),
                Drv(9, "TBD", FamTbd, tbd: true)
            }, Links((TypeL1, FamB)));

            Assert.False(rec.HasMatch);
            Assert.Null(rec.RecommendedCandidate);
            Assert.Equal("B", rec.LinkedFamilyName);
            Assert.Contains("Linked family B", rec.WarningMessage);
            Assert.DoesNotContain("Voltage", rec.WarningMessage);
        }

        [Fact]
        public void LinkedFamily_UnderspecifiedFixture_HintsAtMissingSpec()
        {
            var rec = Recommend(new List<FixtureData> { Fix(1, protocol: null) },
                new List<DriverCandidateInfo> { Drv(2, "B", FamB) },
                Links((TypeL1, FamB)));

            Assert.False(rec.HasMatch);
            Assert.Contains("Voltage and Dimming Protocol", rec.WarningMessage);
        }

        [Fact]
        public void ConflictingLinks_NoMatch()
        {
            var fixtures = new List<FixtureData> { Fix(1, TypeL1), Fix(2, TypeL2, mark: "L2") };
            var rec = Recommend(fixtures,
                new List<DriverCandidateInfo> { Drv(1, "A", FamA), Drv(2, "B", FamB) },
                Links((TypeL1, FamA), (TypeL2, FamB)));

            Assert.False(rec.HasMatch);
            Assert.False(rec.IsTie);
            Assert.Contains("linked to different driver families (A, B)", rec.WarningMessage);
        }

        [Fact]
        public void LinkedPlusUnlinkedFixture_LinkGoverns()
        {
            var fixtures = new List<FixtureData> { Fix(1, TypeL1), Fix(2, TypeL2, mark: "L2") };
            var rec = Recommend(fixtures,
                new List<DriverCandidateInfo> { Drv(1, "A", FamA), Drv(2, "B", FamB) },
                Links((TypeL1, FamB)));

            Assert.True(rec.HasMatch);
            Assert.False(rec.IsTie);
            Assert.Equal("B", rec.RecommendedCandidate.FamilyName);
        }

        [Fact]
        public void LinkOnOtherType_LeavesThisCircuitTied()
        {
            // Linking L2 → B reserves nothing: an L1-only circuit still ties.
            var rec = Recommend(new List<FixtureData> { Fix(1, TypeL1) },
                new List<DriverCandidateInfo> { Drv(1, "A", FamA), Drv(2, "B", FamB) },
                Links((TypeL2, FamB)));

            Assert.True(rec.IsTie);
        }

        [Fact]
        public void DriverLinks_From_SkipsBrokenLinks()
        {
            var links = DriverLinks.From(new[]
            {
                new DriverLink { FixtureTypeRef = new ElementRef(TypeL1), DriverFamilyRef = new ElementRef(FamA) },
                new DriverLink { FixtureTypeRef = new ElementRef(TypeL2), DriverFamilyRef = ElementRef.None, IsBroken = true }
            });

            Assert.Equal(1, links.Count);
            Assert.True(links.TryGetFamily(new ElementRef(TypeL1), out var fam));
            Assert.Equal(FamA, fam.Value);
            Assert.False(links.TryGetFamily(new ElementRef(TypeL2), out _));
        }

        // ── Classifier ───────────────────────────────────────────────────────────────────────────────

        private static DriverRecommendation TieRec(DriverCandidateInfo a, DriverCandidateInfo b)
            => new DriverRecommendation
            {
                HasMatch = true,
                RecommendedCandidate = a,
                DriverCount = 1,
                TiedCandidates = new List<DriverCandidateInfo> { a, b }
            };

        [Theory]
        [InlineData(0, 0)]   // would be NotDeployed
        [InlineData(1, 1)]   // would be Ok / Stale / Rebuild
        [InlineData(2, 1)]   // would be Rebuild (count)
        [InlineData(2, 2)]   // would be Rebuild (mixed)
        public void Classifier_TieTakesPrecedence(int placed, int distinct)
        {
            var a = Drv(1, "A", FamA);
            var b = Drv(2, "B", FamB);
            var result = StaleClassifier.Classify(placed, distinct, placed > 0 ? b : null, TieRec(a, b));

            Assert.Equal(RpsStatus.Tie, result.Status);
            Assert.Null(result.RebuildReason);
        }

        [Fact]
        public void Classifier_DmxStillFirst()
        {
            var result = StaleClassifier.Classify(0, 0, null,
                TieRec(Drv(1, "A", FamA), Drv(2, "B", FamB)), isDmxDecoderManaged: true);
            Assert.Equal(RpsStatus.DmxManaged, result.Status);
        }

        // ── Deferral signature ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void Signature_NonTie_UnchangedFormula()
        {
            var data = new RpsCircuitData
            {
                Status = RpsStatus.Stale, PlacedTypeName = "A-60W", PlacedCount = 1,
                DistinctPlacedTypeCount = 1, RecommendedTypeName = "A-30W", RecommendedCount = 1,
                // Populated tie list must NOT leak into a non-tie signature.
                TiedCandidates = new List<DriverCandidateInfo> { Drv(1, "A", FamA), Drv(2, "B", FamB) }
            };

            Assert.Equal("Stale|A-60W|1|1|A-30W|1", RpsDeferral.Signature(data));
        }

        [Fact]
        public void Signature_Tie_AppendsSortedFamilies()
        {
            var data = new RpsCircuitData
            {
                Status = RpsStatus.Tie, PlacedTypeName = "", PlacedCount = 0,
                DistinctPlacedTypeCount = 0, RecommendedTypeName = "", RecommendedCount = 1,
                TiedCandidates = new List<DriverCandidateInfo> { Drv(2, "beta", FamB), Drv(1, "Alpha", FamA) }
            };

            Assert.Equal("Tie||0|0||1|tie:Alpha,beta", RpsDeferral.Signature(data));
        }

        // ── Driver Match family comparison ───────────────────────────────────────────────────────────

        private static DriverFamilyOption Family(string name, params DriverCandidateInfo[] types)
            => new DriverFamilyOption
            {
                FamilyName = name,
                FamilyRef = types.Length > 0 ? types[0].FamilyRef : ElementRef.None,
                IsTbd = types.Any(t => t.IsTbd),
                Types = types.ToList()
            };

        [Fact]
        public void CompareFamily_CountsMatchingTypes()
        {
            var fam = Family("B",
                Drv(1, "B", FamB, power: 60, sub: 60),
                Drv(2, "B", FamB, power: 96, sub: 96, protocol: "DALI"),
                Drv(3, "B", FamB, power: 120, sub: 60));

            var report = DriverMatchDiagnostic.CompareFamily(Fix(1), fam);

            Assert.Equal(3, report.TotalCount);
            Assert.Equal(2, report.MatchingCount);
            Assert.True(report.Matches);
            Assert.StartsWith("✓ 2 of 3 types can serve L1.", report.Summary);
            Assert.False(report.Rows[1].Matches);
            Assert.Equal(MatchVerdict.Fail, report.Rows[1].Dimming.Verdict);
            Assert.StartsWith("Dimming Protocol:", report.Rows[1].Notes);
            Assert.Equal("120 W (2 × 60 W)", report.Rows[2].Capacity.DriverValue);
        }

        [Fact]
        public void CompareFamily_NoneMatch_ReportsMostCommonFailure()
        {
            var fam = Family("B",
                Drv(1, "B", FamB, protocol: "DALI"),
                Drv(2, "B", FamB, protocol: "DALI"),
                Drv(3, "B", FamB, volts: "12"));

            var report = DriverMatchDiagnostic.CompareFamily(Fix(1), fam);

            Assert.Equal(0, report.MatchingCount);
            Assert.False(report.Matches);
            Assert.StartsWith("✗ No type in this family matches — most common: Dimming Protocol", report.Summary);
        }

        [Fact]
        public void CompareFamily_TbdFamily_Wildcard()
        {
            var report = DriverMatchDiagnostic.CompareFamily(Fix(1), Family("TBD", Drv(9, "TBD", FamTbd, tbd: true)));

            Assert.Equal(1, report.MatchingCount);
            Assert.Contains("TBD placeholder", report.Summary);
            Assert.Contains("can't be linked", report.Summary);
        }

        [Fact]
        public void CompareFamily_NullInputs_Prompt()
        {
            var report = DriverMatchDiagnostic.CompareFamily(null, null);
            Assert.Empty(report.Rows);
            Assert.False(report.Matches);
            Assert.StartsWith("Select", report.Summary);
        }
    }
}
