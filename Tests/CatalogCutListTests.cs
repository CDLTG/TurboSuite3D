using System.Collections.Generic;
using System.Linq;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using Xunit;

namespace TurboSuite.Tests.Docs
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for the Counts catalog length-token / cut-list engine
    //  (Core/Docs/Services/CatalogLengthTokenResolver.cs). Drives purchase quantities on a client
    //  deliverable, so every branch is pinned here.
    //
    //  For me (Claude), not the user: expected values that aren't self-evident carry their derivation
    //  inline (`// 100/12=8`, `// 48*4=192 exact`). If a test breaks, first re-derive from the comment
    //  against the source — don't blindly "fix" the assertion. The grammar/priority rules live in the
    //  resolver's own doc-comments; this file is the executable restatement of them.
    //
    //  Order-independence: SplitInstance/CoverInstance/PoolCoverSlot callers pool results into a dict,
    //  so multiset (sorted) equality is the contract, never emission order. Assert on sorted lists.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Render maps one cut length (inches) into each of the 8 documented format strings.
    /// Feet formats truncate via integer divide (÷12); the split forms carry the inch remainder.</summary>
    public class LengthTokenRenderTests
    {
        // inches=100 → 100/12 = 8 ft, remainder 100%12 = 4 in.
        [Theory]
        [InlineData("{xx}", 100, "100")]
        [InlineData("{xx\"}", 100, "100\"")]
        [InlineData("{xxIN}", 100, "100IN")]
        [InlineData("{ft}", 100, "8")]          // 100/12 truncates to 8
        [InlineData("{xx'}", 100, "8'")]
        [InlineData("{xxFT}", 100, "8FT")]
        [InlineData("{xx'-xx\"}", 100, "8'-4\"")]   // 8 ft 4 in
        [InlineData("{xxFT-xxIN}", 100, "8FT-4IN")]
        public void Resolve_RendersEachFormat(string template, int inches, string expected)
            => Assert.Equal(expected, CatalogLengthTokenResolver.Resolve(template, inches));

        [Fact]
        public void Resolve_ReplacesEveryTokenInstance_WithSameCutSize()
        {
            // All tokens in a template render the CUT size, not the original instance size.
            Assert.Equal("A48\"-B48\"", CatalogLengthTokenResolver.Resolve("A{xx\"}-B{xx\"}", 48));
        }

        [Fact]
        public void Resolve_FeetFormat_TruncatesSubFootRemainder()
        {
            // 47/12 = 3 (not 3.9…); the remainder is dropped by the unitless-feet forms.
            Assert.Equal("3", CatalogLengthTokenResolver.Resolve("{ft}", 47));
            Assert.Equal("3'-11\"", CatalogLengthTokenResolver.Resolve("{xx'-xx\"}", 47));
        }
    }

    /// <summary>HasToken gates the whole expansion path — a false negative silently skips cut-listing.</summary>
    public class LengthTokenDetectionTests
    {
        [Theory]
        [InlineData("TAPE-{xx\"}", true)]
        [InlineData("T-{ft}", true)]
        [InlineData("T-{xx,max=48}", true)]
        [InlineData("PLAIN-SKU-123", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("{xy}", false)]        // not a length token shape
        [InlineData("literal {braces}", false)]
        public void HasToken(string? catalog, bool expected)
            => Assert.Equal(expected, CatalogLengthTokenResolver.HasToken(catalog));
    }

    /// <summary>Consumer-facing fixture schedule: every length token collapses to <c>[*]</c>,
    /// leaving the author's surrounding characters (framing dashes, prefix/suffix) intact.</summary>
    public class StripTokensToPlaceholderTests
    {
        [Theory]
        [InlineData("ILP-{xx\",max=48}-30K", "ILP-[*]-30K")]
        [InlineData("T-{ft}", "T-[*]")]
        [InlineData("{xx'-xx\"}", "[*]")]
        [InlineData("A-{xx}-B-{xx}-C", "A-[*]-B-[*]-C")]  // every instance replaced
        [InlineData("PLAIN-SKU-123", "PLAIN-SKU-123")]    // untokenized passes through
        [InlineData("{xy}", "{xy}")]                       // not a length token — untouched
        [InlineData("", "")]
        [InlineData(null, "")]
        public void Strip(string? catalog, string expected)
            => Assert.Equal(expected, CatalogLengthTokenResolver.StripTokensToPlaceholder(catalog));
    }

    /// <summary>Greedy made-to-length split (max=N): full N-sized cuts plus one remainder.</summary>
    public class SplitInstanceTests
    {
        private static List<int> Split(int len, int? max)
            => CatalogLengthTokenResolver.SplitInstance(len, max).ToList();

        [Fact] public void FillMax_LeavesRemainder() => Assert.Equal(new[] { 91, 29 }, Split(120, 91)); // 120 = 91 + 29
        [Fact] public void ExactMultiple_NoRemainder() => Assert.Equal(new[] { 91, 91 }, Split(182, 91)); // 182 = 91*2
        [Fact] public void EqualToMax_SinglePiece() => Assert.Equal(new[] { 91 }, Split(91, 91));
        [Fact] public void UnderMax_SinglePiece() => Assert.Equal(new[] { 40 }, Split(40, 91));
        [Fact] public void NoMax_WholeInstance() => Assert.Equal(new[] { 120 }, Split(120, null));
        [Fact] public void NonPositive_Empty() => Assert.Empty(Split(0, 91));

        // min= floor: a sub-minimum piece (trailing remainder, or a whole under-min instance)
        // is clamped UP to the floor. Full max-sized pieces are untouched (validation bars min>max).
        private static List<int> Split(int len, int? max, int? min)
            => CatalogLengthTokenResolver.SplitInstance(len, max, min).ToList();

        [Fact] public void Min_ClampsShortRemainderUp() => Assert.Equal(new[] { 197, 12 }, Split(200, 197, 12)); // rem 3 → 12
        [Fact] public void Min_ClampsWholeUnderMinInstanceUp() => Assert.Equal(new[] { 12 }, Split(8, 197, 12));  // 8 ≤ max → single piece 8 → 12
        [Fact] public void Min_NoMax_ClampsWholeInstance() => Assert.Equal(new[] { 12 }, Split(8, null, 12));     // plain + min
        [Fact] public void Min_LegalRemainder_Unchanged() => Assert.Equal(new[] { 197, 40 }, Split(237, 197, 12));// rem 40 ≥ 12
        [Fact] public void Min_ExactMultiple_NoClampNeeded() => Assert.Equal(new[] { 197, 197 }, Split(394, 197, 12));
        [Fact] public void Min_InstanceEqualsMax_SinglePiece() => Assert.Equal(new[] { 197 }, Split(197, 197, 12));

        // granularity: made-to-length pieces round UP to the orderable increment (12 for whole-foot
        // formats); full max-sized sticks stay put because validation forces max onto the increment.
        private static List<int> Split(int len, int? max, int? min, int gran)
            => CatalogLengthTokenResolver.SplitInstance(len, max, min, gran).ToList();

        [Fact] public void Gran_RoundsRemainderUpToFoot() => Assert.Equal(new[] { 96, 36 }, Split(126, 96, null, 12)); // rem 30 → 36
        [Fact] public void Gran_RoundsWholeInstanceUp_NeverZero() => Assert.Equal(new[] { 12 }, Split(8, null, null, 12)); // 8 → 12, not 0
        [Fact] public void Gran_FullSticksNotRounded() => Assert.Equal(new[] { 96, 96, 12 }, Split(200, 96, null, 12));    // rem 8 → 12; 96s untouched
        [Fact] public void Gran_ExactFoot_Unchanged() => Assert.Equal(new[] { 24 }, Split(24, 96, null, 12));            // 24 = 2' already
        [Fact] public void Gran_ClampsToMinThenRoundsToFoot() => Assert.Equal(new[] { 96, 36 }, Split(130, 96, 24, 12)); // rem 34 → 36 (≥ min 24)
        [Fact] public void Gran_One_IsNoOp() => Assert.Equal(new[] { 96, 30 }, Split(126, 96, null, 1));                 // inch format unaffected
    }

    /// <summary>Discrete-stock cover (sizes=): exact-fit wins on principle even when it costs more
    /// pieces; else fewest pieces, tie-broken on least overage. The three canonical resolver examples.</summary>
    public class CoverInstanceTests
    {
        private static readonly IReadOnlyList<int> S_94_48 = new[] { 94, 48 }; // descending, as ParseSizes yields

        private static List<int> Cover(int len, IReadOnlyList<int> sizes)
            => CatalogLengthTokenResolver.CoverInstance(len, sizes).OrderBy(x => x).ToList();

        [Fact]
        public void ExactFit_PreferredOverFewerPieces()
        {
            // L=192: 48*4 = 192 exact (4 pcs) beats 94*2 = 188<192 and the 94+94+48=236 cover (3 pcs, +44).
            Assert.Equal(new[] { 48, 48, 48, 48 }, Cover(192, S_94_48));
        }

        [Fact]
        public void NoExact_FewestPieces()
        {
            // L=264: no exact combo of {94,48}. 94*3 = 282 (3 pcs, +18) is the fewest-piece cover.
            Assert.Equal(new[] { 94, 94, 94 }, Cover(264, S_94_48));
        }

        [Fact]
        public void NoExact_FewestPieces_ThenLeastOverage()
        {
            // L=200: 94+94+48 = 236 (3 pcs, +36) beats 48*5 = 240 (5 pcs) — pieces dominate overage.
            Assert.Equal(new[] { 48, 94, 94 }, Cover(200, S_94_48));
            Assert.Equal(236, Cover(200, S_94_48).Sum());
        }

        [Fact] public void NonPositive_Empty() => Assert.Empty(Cover(0, S_94_48));
        [Fact] public void NoSizes_Empty() => Assert.Empty(Cover(100, new int[0]));
    }

    /// <summary>Pool cover: trailing partial pieces are served from a shared offcut pool
    /// (min 18" reusable), so a slot buys fewer sticks than the non-pooled sizes= mode would.</summary>
    public class PoolCoverSlotTests
    {
        // sizes=[96]; instances L=100 (needs 96+96, tail 92) and L=20 (needs a 96, but the 92 offcut serves it).
        // Pooled: the L=20's 20" comes out of the 92" offcut → 2 sticks total. Non-pooled sizes= would buy 3.
        [Fact]
        public void ReusesOffcutAcrossInstances()
        {
            var buckets = new Dictionary<int, int> { { 100, 1 }, { 20, 1 } };
            var sticks = CatalogLengthTokenResolver.PoolCoverSlot(buckets, new[] { 96 });
            Assert.Equal(2, sticks[96]);
        }

        [Fact]
        public void PoolBeatsNonPooledSizes_ForSameInstances()
        {
            // Contrast: the sizes= (per-instance) path buys L=100→2 sticks + L=20→1 stick = 3.
            int nonPooled =
                CatalogLengthTokenResolver.CoverInstance(100, new[] { 96 }).Count()
              + CatalogLengthTokenResolver.CoverInstance(20, new[] { 96 }).Count();
            int pooled = CatalogLengthTokenResolver.PoolCoverSlot(
                new Dictionary<int, int> { { 100, 1 }, { 20, 1 } }, new[] { 96 })[96];
            Assert.Equal(3, nonPooled);
            Assert.Equal(2, pooled);
        }

        [Fact]
        public void ShortTail_BecomesScrap_NotPooled()
        {
            // sizes=[96]; two L=90 instances. Each opens a 96 stick, tail 6" < 18" min → scrapped, not reused.
            var sticks = CatalogLengthTokenResolver.PoolCoverSlot(
                new Dictionary<int, int> { { 90, 2 } }, new[] { 96 });
            Assert.Equal(2, sticks[96]);
        }

        [Fact]
        public void EmptyInputs_ZeroStickers()
        {
            var sticks = CatalogLengthTokenResolver.PoolCoverSlot(new Dictionary<int, int>(), new[] { 96 });
            Assert.Equal(0, sticks[96]);
        }
    }

    /// <summary>ExpandSlot is the single source of truth wiring template + buckets → (SKU, qty) rows,
    /// sorted by cut length ascending. Covers the untokenized, blank, max, sizes, and pool branches.</summary>
    public class ExpandSlotTests
    {
        private static CountsFixtureModel Fx(string slot0, int count = 0, params (int inches, int n)[] buckets)
        {
            var f = new CountsFixtureModel { Count = count };
            f.CatalogNumbers[0] = slot0;
            foreach (var (inches, n) in buckets) f.LinearLengthBuckets[inches] = n;
            return f;
        }

        private static List<(string Sku, int Qty)> Expand(CountsFixtureModel f)
            => CatalogLengthTokenResolver.ExpandSlot(f, 0).ToList();

        [Fact]
        public void Untokenized_YieldsTemplateWithFixtureCount()
        {
            Assert.Equal(new[] { ("ABC-123", 5) }, Expand(Fx("ABC-123", count: 5)));
        }

        [Fact]
        public void Blank_YieldsNothing()
        {
            Assert.Empty(Expand(Fx("", count: 5)));
            Assert.Empty(CatalogLengthTokenResolver.ExpandSlot(new CountsFixtureModel { Count = 5 }, 0));
        }

        [Fact]
        public void MaxMode_ExplodesPerCutLength_SortedAscending()
        {
            // L=100, max=48 → 48,48,4. Rows keyed by cut, ascending: 4"(×1), 48"(×2).
            var rows = Expand(Fx("TAPE-{xx\",max=48}", buckets: (100, 1)));
            Assert.Equal(new[] { ("TAPE-4\"", 1), ("TAPE-48\"", 2) }, rows);
        }

        [Fact]
        public void MaxWithMin_ClampsShortRemainder_AndPoolsIntoTheFloorRow()
        {
            // PLX tape shape: {xx",max=197,min=12}. L=200 → 197 + (rem 3 clamped to 12).
            // Rows keyed by cut, ascending: 12"(×1), 197"(×1).
            var rows = Expand(Fx("PLX-{xx\",max=197,min=12}", buckets: (200, 1)));
            Assert.Equal(new[] { ("PLX-12\"", 1), ("PLX-197\"", 1) }, rows);
        }

        [Fact]
        public void MaxWithMin_ClampedRemaindersPoolWithNaturalFloorCuts()
        {
            // L=200 → [197,12(clamped from 3)]; L=12 → [12]. The two 12" cuts pool into one row ×2.
            var rows = Expand(Fx("PLX-{xx\",max=197,min=12}", buckets: new[] { (200, 1), (12, 1) }));
            Assert.Equal(new[] { ("PLX-12\"", 2), ("PLX-197\"", 1) }, rows);
        }

        [Fact]
        public void SizesMode_PoolsIdenticalCutsAcrossInstanceCount()
        {
            // Two L=192 instances, sizes=94|48 → each covers as 48*4; qty = 4 cuts × 2 instances = 8.
            var rows = Expand(Fx("T-{xx,sizes=94|48}", buckets: (192, 2)));
            Assert.Equal(new[] { ("T-48", 8) }, rows);
        }

        [Fact]
        public void PoolMode_UsesSharedOffcutPool()
        {
            // pool=96 over L=100 + L=20 → 2 sticks of 96 (offcut reuse), one row.
            var rows = Expand(Fx("P-{xx,pool=96}", buckets: new[] { (100, 1), (20, 1) }));
            Assert.Equal(new[] { ("P-96", 2) }, rows);
        }

        [Fact]
        public void WholeFootFormat_RoundsCutsUpToWholeFeet_DistinctLengthsAreDistinctRows()
        {
            // xx' orders in whole feet, rounding UP. A 10" instance clamps to min 24" = 2'; a 30"
            // instance rounds up to 3' (NOT floored to 2'). They are different ordered parts, so the
            // quote shows one 2' AND one 3' — the field's "duplicate 2'-DAL" was a 30" piece the old
            // floor mislabeled as 2'.
            var rows = Expand(Fx("LITE-{xx',min=24,max=96}-DAL", buckets: new[] { (10, 1), (30, 1) }));
            Assert.Equal(new[] { ("LITE-2'-DAL", 1), ("LITE-3'-DAL", 1) }, rows);
        }

        [Fact]
        public void WholeFootFormat_GenuineDuplicateCutsPoolIntoOneRow()
        {
            // The user's "if it really WAS a duplicate" case: an 8" instance clamps up to 24" (2')
            // and a natural 24" instance is already 2'. Both land on 24" and pool into a single
            // 2'×2 row — real duplicates still merge, now at the inch-pooling level.
            var rows = Expand(Fx("LITE-{xx',min=24,max=96}-DAL", buckets: new[] { (8, 1), (24, 1) }));
            Assert.Equal(new[] { ("LITE-2'-DAL", 2) }, rows);
        }

        [Fact]
        public void WholeFootFormat_ShortCutNeverRendersZeroFeet()
        {
            // A bare {xx'} whole instance of 8" rounds up to 1' — never the un-orderable 0'.
            var rows = Expand(Fx("X-{xx'}", buckets: (8, 1)));
            Assert.Equal(new[] { ("X-1'", 1) }, rows);
        }

        [Fact]
        public void WholeFootFormat_FullSticksUnrounded_RemainderRoundsUp_SortedAscending()
        {
            // L=200, max=96 → two full 8' sticks (untouched) + an 8" remainder rounded up to 1'.
            var rows = Expand(Fx("X-{xx',max=96}", buckets: (200, 1)));
            Assert.Equal(new[] { ("X-1'", 1), ("X-8'", 2) }, rows);
        }
    }

    /// <summary>ExpandTokenBuckets is the shared split core behind both ExpandSlot (rebuild) and the
    /// Worksheet-sync re-derivation. These pin that the two never drift: the buckets it yields,
    /// rendered through Resolve, must equal ExpandSlot's rows for every cut mode — pool= included,
    /// which the old sync path silently mishandled.</summary>
    public class ExpandTokenBucketsTests
    {
        private static CountsFixtureModel Fx(string slot0, int count, params (int inches, int n)[] buckets)
        {
            var f = new CountsFixtureModel { Count = count };
            f.CatalogNumbers[0] = slot0;
            foreach (var (inches, n) in buckets) f.LinearLengthBuckets[inches] = n;
            return f;
        }

        // The SKU/qty a caller gets by rendering ExpandTokenBuckets must equal ExpandSlot's rows.
        [Theory]
        [InlineData("PLX-{xx\",max=197,min=12}")]   // max + min modifier
        [InlineData("T-{xx\",sizes=94|48}")]         // discrete stock
        [InlineData("P-{xx\",pool=94|48}")]          // pooled offcuts — the drift-prone mode
        [InlineData("B-{xx\"}")]                     // bare token, no mode
        [InlineData("F-{xx',min=24,max=96}")]        // truncating feet — distinct inches share a SKU
        public void RenderedBuckets_MatchExpandSlot(string template)
        {
            var f = Fx(template, count: 0, buckets: new[] { (200, 1), (130, 1), (48, 2) });
            var viaSlot = CatalogLengthTokenResolver.ExpandSlot(f, 0).ToList();
            var viaBuckets = CatalogLengthTokenResolver
                .ExpandTokenBuckets(template, f.LinearLengthBuckets)
                .Select(b => (CatalogLengthTokenResolver.Resolve(template, b.CutInches), b.Qty))
                .ToList();
            Assert.Equal(viaSlot, viaBuckets);
        }

        [Fact]
        public void PoolMode_YieldsStickBuckets_NotWholeInstances()
        {
            // Regression guard for the old sync path, which lacked a pool= branch and would have
            // emitted whole-instance lengths. pool=96 over L=100 + L=20 → 2 sticks of 96", one bucket.
            var buckets = CatalogLengthTokenResolver
                .ExpandTokenBuckets("P-{xx\",pool=96}", new Dictionary<int, int> { { 100, 1 }, { 20, 1 } })
                .ToList();
            Assert.Equal(new[] { (96, 2) }, buckets);
        }
    }

    /// <summary>Validate is the gatekeeper for malformed templates — its rejections become the
    /// user-facing "fix the family and re-export" errors, so pin the trigger for each.</summary>
    public class LengthTokenValidateTests
    {
        private static void Bad(string template)
            => Assert.Throws<CatalogLengthTokenParseException>(() => CatalogLengthTokenResolver.Validate(template));

        [Theory]
        [InlineData("{xx3}")]                 // malformed token shape
        [InlineData("T-{xx,foo}")]            // option without '='
        [InlineData("T-{xx,zzz=5}")]          // unknown option key
        [InlineData("T-{xx,max=abc}")]        // non-integer max
        [InlineData("T-{xx,max=0}")]          // non-positive max
        [InlineData("T-{xx,sizes=}")]         // empty sizes list
        [InlineData("T-{xx,sizes=94|x}")]     // non-integer sizes entry
        [InlineData("T-{xx,sizes=48|48}")]    // duplicate sizes entry
        [InlineData("T-{xx,max=48,sizes=94}")]// mutually exclusive
        [InlineData("T-{xx,pool=96,sizes=48}")]
        [InlineData("T-{xx,min=abc}")]        // non-integer min
        [InlineData("T-{xx,min=0}")]          // non-positive min
        [InlineData("T-{xx,max=48,min=96}")]  // min > max
        [InlineData("T-{xx,sizes=94,min=12}")]// min not allowed with sizes
        [InlineData("T-{xx,pool=96,min=12}")] // min not allowed with pool
        [InlineData("T-{xx',max=90}")]        // whole-foot format: max not a foot multiple
        [InlineData("T-{xx',min=18,max=96}")] // whole-foot format: min not a foot multiple
        [InlineData("T-{ft,max=90}")]         // ft is whole-foot too
        [InlineData("T-{xxFT,max=90}")]       // xxFT is whole-foot too
        [InlineData("T-{xx',sizes=94|48}")]   // whole-foot format: sizes entry not a foot multiple
        [InlineData("T-{xx',pool=90}")]       // whole-foot format: pool entry not a foot multiple
        public void RejectsMalformed(string template) => Bad(template);

        [Theory]
        [InlineData("TAPE-{xx\"}")]
        [InlineData("T-{xx,max=48}")]
        [InlineData("T-{xx,sizes=94|48}")]
        [InlineData("T-{xx,pool=96|48}")]
        [InlineData("T-{xx\",max=197,min=12}")] // max + min modifier
        [InlineData("T-{xx\",min=197,max=197}")]// min order-independent; min == max is legal
        [InlineData("T-{xx\",min=12}")]          // min on a bare token (no mode)
        [InlineData("T-{xx',min=24,max=96}")]    // whole-foot format: foot-multiple max/min OK
        [InlineData("T-{xx',sizes=96|48}")]      // whole-foot format: foot-multiple stock OK
        [InlineData("PLAIN-SKU")]             // no token → nothing to validate
        [InlineData("")]
        [InlineData(null)]
        public void AcceptsWellFormed(string? template) => CatalogLengthTokenResolver.Validate(template);
    }

    /// <summary>Option parsers read the FIRST token's modifier; size lists come back descending
    /// (CoverInstance's DP assumes sizes[0] is the max). Keys are cross-exclusive by construction.</summary>
    public class LengthTokenOptionParseTests
    {
        [Fact] public void ParseMaxInches_ReadsValue() => Assert.Equal(48, CatalogLengthTokenResolver.ParseMaxInches("{xx,max=48}"));
        [Fact] public void ParseMaxInches_NoOption_Null() => Assert.Null(CatalogLengthTokenResolver.ParseMaxInches("{xx}"));

        [Fact] public void ParseMinInches_ReadsValue() => Assert.Equal(12, CatalogLengthTokenResolver.ParseMinInches("{xx\",max=197,min=12}"));
        [Fact] public void ParseMinInches_OrderIndependent() => Assert.Equal(12, CatalogLengthTokenResolver.ParseMinInches("{xx\",min=12,max=197}"));
        [Fact] public void ParseMinInches_NoOption_Null() => Assert.Null(CatalogLengthTokenResolver.ParseMinInches("{xx,max=48}"));

        [Fact]
        public void ParseSizes_ReturnsDescending()
            => Assert.Equal(new[] { 94, 48 }, CatalogLengthTokenResolver.ParseSizes("{xx,sizes=48|94}"));

        [Fact] public void ParseSizes_OnPoolToken_Null() => Assert.Null(CatalogLengthTokenResolver.ParseSizes("{xx,pool=96}"));

        [Fact]
        public void ParsePool_ReturnsDescending()
            => Assert.Equal(new[] { 96, 48 }, CatalogLengthTokenResolver.ParsePool("{xx,pool=48|96}"));

        [Fact] public void ParsePool_OnSizesToken_Null() => Assert.Null(CatalogLengthTokenResolver.ParsePool("{xx,sizes=96}"));
    }

    /// <summary>Waste analyzer feeds the hidden Calculations sheet. Only sizes= (and pool=) can strand
    /// material; max/plain always supply exactly what's used.</summary>
    public class WasteAnalyzerTests
    {
        private static CountsFixtureModel Fx(string slot0, params (int inches, int n)[] buckets)
        {
            var f = new CountsFixtureModel();
            f.CatalogNumbers[0] = slot0;
            foreach (var (inches, n) in buckets) f.LinearLengthBuckets[inches] = n;
            return f;
        }

        [Fact]
        public void SizesMode_ReportsOverageAsWaste()
        {
            // L=200, sizes=94|48 → supplied 236, used 200, waste 36.
            var w = CatalogWasteAnalyzer.ComputeSlotWaste(Fx("{xx,sizes=94|48}", (200, 1)), 0);
            Assert.Equal("sizes", w.Mode);
            Assert.Equal(1, w.InstanceCount);
            Assert.Equal(200, w.UsedInches);
            Assert.Equal(236, w.SuppliedInches);
            Assert.Equal(36, w.WasteInches);
        }

        [Fact]
        public void MaxMode_NoWaste()
        {
            // max cuts tile the instance exactly (last piece is the remainder), so supplied == used.
            var w = CatalogWasteAnalyzer.ComputeSlotWaste(Fx("{xx,max=48}", (100, 1)), 0);
            Assert.Equal("max", w.Mode);
            Assert.Equal(0, w.WasteInches);
        }

        [Fact]
        public void MaxWithMin_ClampedRemainderCountsAsWaste()
        {
            // L=200, max=197, min=12 → supplied 197+12 = 209, used 200, waste 9 (the 3"→12" clamp).
            var w = CatalogWasteAnalyzer.ComputeSlotWaste(Fx("{xx\",max=197,min=12}", (200, 1)), 0);
            Assert.Equal("max", w.Mode);
            Assert.Equal(200, w.UsedInches);
            Assert.Equal(209, w.SuppliedInches);
            Assert.Equal(9, w.WasteInches);
        }

        [Fact]
        public void WholeFootFormat_SuppliedCountsRoundedUpFeet()
        {
            // L=30 under {xx',max=96} orders as 3' (36"), not 30" — the foot-rounding overage is
            // real waste the Calc sheet must show. used 30, supplied 36, waste 6.
            var w = CatalogWasteAnalyzer.ComputeSlotWaste(Fx("{xx',max=96}", (30, 1)), 0);
            Assert.Equal("max", w.Mode);
            Assert.Equal(30, w.UsedInches);
            Assert.Equal(36, w.SuppliedInches);
            Assert.Equal(6, w.WasteInches);
        }

        [Fact]
        public void PlainToken_NoWaste()
        {
            // Bare {xx} with no modifier: each instance supplied whole. used=supplied over N instances.
            var w = CatalogWasteAnalyzer.ComputeSlotWaste(Fx("{xx}", (100, 2)), 0);
            Assert.Equal("plain", w.Mode);
            Assert.Equal(200, w.UsedInches);
            Assert.Equal(0, w.WasteInches);
        }

        [Fact]
        public void NoToken_EmptyStats()
        {
            var w = CatalogWasteAnalyzer.ComputeSlotWaste(Fx("PLAIN"), 0);
            Assert.Equal(string.Empty, w.Mode);
            Assert.Equal(0, w.InstanceCount);
        }
    }

    /// <summary>The batch validator's two cross-checks beyond token syntax: a token needs positive
    /// Linear Length, and a length token can't share a slot with a Catalog Qty override.</summary>
    public class LengthTokenBatchValidatorTests
    {
        private static CountsFixtureModel Fx(string catalog0, string? qty0 = null, params (int inches, int n)[] buckets)
        {
            var f = new CountsFixtureModel { TypeMark = "T1" };
            f.CatalogNumbers[0] = catalog0;
            f.CatalogQtys[0] = qty0!;
            foreach (var (inches, n) in buckets) f.LinearLengthBuckets[inches] = n;
            return f;
        }

        [Fact]
        public void Token_WithNoLinearLength_Rejected()
        {
            var ex = Assert.Throws<CatalogLengthTokenValidationException>(
                () => CatalogLengthTokenValidator.ValidateOrThrow(new[] { Fx("{xx\"}") }));
            Assert.Contains("positive Linear Length", ex.Errors.Single().Reason);
        }

        [Fact]
        public void Token_CombinedWithCatalogQty_Rejected()
        {
            var ex = Assert.Throws<CatalogLengthTokenValidationException>(
                () => CatalogLengthTokenValidator.ValidateOrThrow(new[] { Fx("{xx\"}", qty0: "5", buckets: (48, 1)) }));
            Assert.Contains("cannot be combined", ex.Errors.Single().Reason);
        }

        [Fact]
        public void ValidToken_Passes()
        {
            CatalogLengthTokenValidator.ValidateOrThrow(new[] { Fx("{xx,max=48}", buckets: (100, 1)) });
        }

        [Fact]
        public void NoToken_Ignored()
        {
            CatalogLengthTokenValidator.ValidateOrThrow(new[] { Fx("PLAIN-SKU", qty0: "5") });
        }
    }
}
