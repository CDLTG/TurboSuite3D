using TurboSuite.Name;
using Xunit;

namespace TurboSuite.Tests
{
    /// <summary>
    /// Oracle tests for TurboName's Revit-free link-scope matching (Core/Name/CadLinkScope.cs) — the
    /// disambiguation that kills TurboName-9 (a plan + RCP sharing a room-name layer double-seeding each room).
    /// </summary>
    public class CadLinkScopeTests
    {
        // ── Includes: blank = all links; else case-insensitive whole-filename match ──

        [Theory]
        [InlineData(null, "Floor Plan.dwg")]
        [InlineData("", "Floor Plan.dwg")]
        [InlineData("   ", "Floor Plan.dwg")]
        public void Includes_BlankScope_MatchesEveryLink(string? scope, string dwg)
            => Assert.True(CadLinkScope.Includes(scope, dwg));

        [Theory]
        [InlineData("Floor Plan.dwg", "Floor Plan.dwg")]
        [InlineData("floor plan.dwg", "FLOOR PLAN.DWG")]   // case-insensitive
        [InlineData("  Floor Plan.dwg  ", "Floor Plan.dwg")] // trims both sides
        public void Includes_MatchingName_ReturnsTrue(string scope, string dwg)
            => Assert.True(CadLinkScope.Includes(scope, dwg));

        [Theory]
        [InlineData("Floor Plan.dwg", "RCP.dwg")]
        [InlineData("Floor Plan.dwg", "")]
        [InlineData("Floor Plan.dwg", null)]
        public void Includes_NonMatchingOrMissingDwg_ReturnsFalse(string scope, string? dwg)
            => Assert.False(CadLinkScope.Includes(scope, dwg));

        // ── ParseScopedLayer: "file|layer" split, bare = legacy (null file) ──

        [Fact]
        public void ParseScopedLayer_Qualified_SplitsFileAndLayer()
        {
            var (file, layer) = CadLinkScope.ParseScopedLayer("Floor Plan.dwg|WALL_INTR");
            Assert.Equal("Floor Plan.dwg", file);
            Assert.Equal("WALL_INTR", layer);
        }

        [Fact]
        public void ParseScopedLayer_Qualified_TrimsBothParts()
        {
            var (file, layer) = CadLinkScope.ParseScopedLayer("  Floor Plan.dwg  |  WALL_INTR  ");
            Assert.Equal("Floor Plan.dwg", file);
            Assert.Equal("WALL_INTR", layer);
        }

        [Theory]
        [InlineData("WALL_INTR", "WALL_INTR")]
        [InlineData("  WALL_INTR  ", "WALL_INTR")]
        public void ParseScopedLayer_Bare_IsLegacyNullFile(string entry, string expectedLayer)
        {
            var (file, layer) = CadLinkScope.ParseScopedLayer(entry);
            Assert.Null(file);
            Assert.Equal(expectedLayer, layer);
        }

        [Fact]
        public void ParseScopedLayer_EmptyFilePart_IsLegacyNullFile()
        {
            var (file, layer) = CadLinkScope.ParseScopedLayer("|WALL_INTR");
            Assert.Null(file);
            Assert.Equal("WALL_INTR", layer);
        }

        // ── MatchesLayer: qualified entry pins its file; bare entry falls back to the legacy scope ──

        [Fact]
        public void MatchesLayer_QualifiedEntry_MatchesOnlyItsOwnFile()
        {
            // Entry scoped to the plan must not match the same layer name in the RCP — this is TurboName-9.
            Assert.True(CadLinkScope.MatchesLayer("Floor Plan.dwg", "WALL_INTR", "wall_intr",
                legacyScope: "", dwgFileName: "Floor Plan.dwg"));
            Assert.False(CadLinkScope.MatchesLayer("Floor Plan.dwg", "WALL_INTR", "WALL_INTR",
                legacyScope: "", dwgFileName: "RCP.dwg"));
        }

        [Fact]
        public void MatchesLayer_BareEntry_HonorsLegacyScope()
        {
            // Bare entry under a blank legacy scope matches any DWG…
            Assert.True(CadLinkScope.MatchesLayer(null, "WALL_INTR", "WALL_INTR",
                legacyScope: "", dwgFileName: "RCP.dwg"));
            // …but a set SourceLinkName narrows it to that file.
            Assert.True(CadLinkScope.MatchesLayer(null, "WALL_INTR", "WALL_INTR",
                legacyScope: "Floor Plan.dwg", dwgFileName: "Floor Plan.dwg"));
            Assert.False(CadLinkScope.MatchesLayer(null, "WALL_INTR", "WALL_INTR",
                legacyScope: "Floor Plan.dwg", dwgFileName: "RCP.dwg"));
        }

        [Fact]
        public void MatchesLayer_DifferentLayerName_NeverMatches()
            => Assert.False(CadLinkScope.MatchesLayer("Floor Plan.dwg", "WALL_INTR", "DOOR",
                legacyScope: "", dwgFileName: "Floor Plan.dwg"));
    }
}
