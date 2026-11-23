using TurboSuite.Shared.Storage;
using Xunit;

namespace TurboSuite.Tests.Shared
{
    /// <summary>
    /// The storage-generation guard and its siblings: which build may save into which project, when a stamp is
    /// written, where a carried-forward store is read from, and when a JSON store is refused.
    /// </summary>
    public class StorageGenerationTests
    {
        // ── CanWrite ──

        [Theory]
        [InlineData(0, 1, true)]   // unstamped project, seed build
        [InlineData(1, 1, true)]   // same generation
        [InlineData(1, 2, true)]   // newer build
        [InlineData(2, 1, false)]  // a newer build stamped it
        public void CanWrite(int doc, int build, bool expected) =>
            Assert.Equal(expected, StorageGenerationGate.CanWrite(doc, build));

        // ── DocGeneration ──

        [Fact]
        public void DocGeneration_Unstamped_IsZero()
        {
            Assert.Equal(StorageGenerationGate.Unstamped, StorageGenerationGate.DocGeneration(new int[0]));
            Assert.Equal(StorageGenerationGate.Unstamped, StorageGenerationGate.DocGeneration(null));
        }

        [Fact]
        public void DocGeneration_IsMaxOverEveryStamp_NotFirst()
        {
            // Append-only stamps + the first-save race leave several; order (ElementId) must not matter.
            Assert.Equal(3, StorageGenerationGate.DocGeneration(new[] { 2, 3, 2 }));
            Assert.Equal(3, StorageGenerationGate.DocGeneration(new[] { 3, 2 }));
        }

        // ── ShouldStamp ──

        [Theory]
        [InlineData(0, 1, false)]  // seed: stamping 1 protects nothing
        [InlineData(1, 1, false)]  // equal: never re-stamp
        [InlineData(0, 2, true)]   // first bump on an unstamped project
        [InlineData(1, 2, true)]
        [InlineData(2, 2, false)]
        [InlineData(3, 2, false)]  // blocked anyway; never stamp downward
        public void ShouldStamp_Project(int doc, int build, bool expected) =>
            Assert.Equal(expected, StorageGenerationGate.ShouldStamp(doc, build, isTemplate: false));

        [Theory]
        [InlineData(0, 2)]
        [InlineData(1, 5)]
        public void ShouldStamp_NeverInTemplate(int doc, int build) =>
            Assert.False(StorageGenerationGate.ShouldStamp(doc, build, isTemplate: true));

        [Theory]
        [InlineData(@"C:\Templates\Firm 2D.rte", true)]
        [InlineData(@"C:\Templates\FIRM.RTE", true)]
        [InlineData(@"C:\Jobs\Job.rvt", false)]
        [InlineData("", false)]       // a project just created from a template, not yet saved
        [InlineData(null, false)]
        public void IsTemplatePath(string? path, bool expected) =>
            Assert.Equal(expected, StorageGenerationGate.IsTemplatePath(path));

        // ── StoreSourceChooser (carry-forward) ──

        [Fact]
        public void Choose_CurrentPresent_WinsOverLegacy()
        {
            var c = StoreSourceChooser.Choose(true, new[] { true, true });
            Assert.Equal(StoreSource.Current, c.Source);
            Assert.Equal(-1, c.LegacyIndex);
        }

        [Fact]
        public void Choose_LegacyOnly_TakesNewestPresent()
        {
            var c = StoreSourceChooser.Choose(false, new[] { false, true, true });
            Assert.Equal(StoreSource.Legacy, c.Source);
            Assert.Equal(1, c.LegacyIndex);
        }

        [Fact]
        public void Choose_Nothing_IsNone()
        {
            Assert.Equal(StoreSource.None, StoreSourceChooser.Choose(false, new[] { false, false }).Source);
            Assert.Equal(StoreSource.None, StoreSourceChooser.Choose(false, null).Source);
        }

        // ── PayloadGate ──

        [Fact]
        public void PayloadGate_ParsedOrAbsent_Writable_UnparsedBlocked()
        {
            Assert.True(PayloadGate.CanWrite(storedPresent: false, parsed: false));  // nothing stored yet
            Assert.True(PayloadGate.CanWrite(storedPresent: true, parsed: true));    // incl. a newer PayloadVersion that parses
            Assert.False(PayloadGate.CanWrite(storedPresent: true, parsed: false));  // never overwrite what we couldn't read
        }
    }
}
