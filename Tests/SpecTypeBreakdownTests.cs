using System.Linq;
using TurboSuite.Schedule.Models;
using TurboSuite.Schedule.Services;
using Xunit;

namespace TurboSuite.Tests.Schedule
{
    /// <summary>
    /// The per-type breakdown behind a Type Mark page: the header's type list and the hover text on a
    /// ⟨varies⟩ / n/a field — plus the rule that only those two states carry a tooltip.
    /// </summary>
    public class SpecTypeBreakdownTests
    {
        private static readonly SpecTypeRef A4 = new SpecTypeRef("Linear", "4ft", true);
        private static readonly SpecTypeRef A8 = new SpecTypeRef("Linear", "8ft", false);
        private static readonly SpecTypeRef B = new SpecTypeRef("Cove", "Std", true);

        private static SpecField Field(string label = "Finish 1") =>
            new SpecField(FieldDef.Roster.First(d => d.Label == label)) { ValueKind = SpecValueKind.Text };

        [Fact]
        public void Summary_GroupsByFamilyInFirstSeenOrder_AndMarksUnplaced()
        {
            Assert.Equal("Linear: 4ft, 8ft (not placed) · Cove: Std",
                SpecTypeBreakdown.Summary(new[] { A4, A8, B }));
        }

        [Fact]
        public void Summary_And_List_Empty()
        {
            Assert.Equal("", SpecTypeBreakdown.Summary(new SpecTypeRef[0]));
            Assert.Null(SpecTypeBreakdown.List(new SpecTypeRef[0]));
        }

        [Fact]
        public void List_OneLabelPerLine()
        {
            Assert.Equal("Linear: 4ft\nLinear: 8ft (not placed)\nCove: Std",
                SpecTypeBreakdown.List(new[] { A4, A8, B }));
        }

        [Fact]
        public void Varies_ListsEachTypesValue_BlankShown()
        {
            var text = SpecTypeBreakdown.Varies(new[] { (A4, "White"), (A8, "") }, SpecValueKind.Text);
            Assert.Contains("Linear: 4ft — White", text);
            Assert.Contains("Linear: 8ft (not placed) — (blank)", text);
        }

        [Fact]
        public void Varies_BooleanRendersYesNo()
        {
            var text = SpecTypeBreakdown.Varies(new[] { (A4, "1"), (B, "0") }, SpecValueKind.Boolean);
            Assert.Contains("Linear: 4ft — Yes", text);
            Assert.Contains("Cove: Std — No", text);
        }

        [Fact]
        public void Missing_NamesOnlyTheTypesLackingTheParameter()
        {
            var text = SpecTypeBreakdown.Missing(new[] { B });
            Assert.Contains("Cove: Std", text);
            Assert.DoesNotContain("Linear", text);
        }

        [Fact]
        public void StorageMismatch_ListsEachTypesKind()
        {
            var text = SpecTypeBreakdown.StorageMismatch(new[] { (A4, "Text"), (B, "Number") });
            Assert.Contains("Linear: 4ft — Text", text);
            Assert.Contains("Cove: Std — Number", text);
        }

        [Fact]
        public void PageCarriesTypes_ForHeader()
        {
            var page = new FixtureTypeSpec("R1", PageKind.Fixture, new[] { Field() }, new[] { A4, B });
            Assert.Equal("Linear: 4ft · Cove: Std", page.TypesSummary);
            Assert.Equal(2, page.Types.Count);
        }

        // ── StateToolTip: only n/a and unresolved ⟨varies⟩ ──

        [Fact]
        public void ToolTip_NullOnNormalField()
        {
            var f = Field();
            f.SetInitialValue("White");
            Assert.Null(f.StateToolTip);
        }

        [Fact]
        public void ToolTip_ShownOnNa()
        {
            var f = Field();
            f.NaDetail = "missing";
            f.IsNa = true;
            Assert.Equal("missing", f.StateToolTip);
        }

        [Fact]
        public void ToolTip_ShownOnVaries_ClearedByEdit_RestoredByDiscard()
        {
            var f = Field();
            f.VariesDetail = "differs";
            f.IsVaries = true;
            Assert.Equal("differs", f.StateToolTip);

            f.Value = "Black";            // typing over ⟨varies⟩ resolves it
            Assert.Null(f.StateToolTip);

            f.ResetToOriginal();          // Discard brings ⟨varies⟩ back
            Assert.Equal("differs", f.StateToolTip);
        }

        [Fact]
        public void ToolTip_GoneAfterSave()
        {
            var f = Field();
            f.VariesDetail = "differs";
            f.IsVaries = true;
            f.Value = "Black";
            f.MarkSaved();
            Assert.Null(f.StateToolTip);
        }
    }
}
