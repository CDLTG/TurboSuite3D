using System.Collections.Generic;
using System.Linq;
using TurboSuite.Schedule.Models;
using TurboSuite.Schedule.Services;
using Xunit;

namespace TurboSuite.Tests.Schedule
{
    /// <summary>
    /// The workbook→model diff. The through-line is <b>no spurious writes</b> (unchanged cells produce
    /// nothing) plus best-effort reporting of everything not applied. Fixtures are built from the real
    /// <see cref="FieldDef.Roster"/> so header→field matching is exercised for real.
    /// </summary>
    public class ScheduleSyncPlannerTests
    {
        // ── fixture builders ──

        private static FieldDef Def(string label, PageKind kind) =>
            FieldDef.Roster.First(d => d.Label == label && d.AppliesTo(kind));

        private static SpecField Field(string label, PageKind kind, SpecValueKind vk, string original,
            bool readOnly = false, bool na = false)
        {
            var f = new SpecField(Def(label, kind)) { ValueKind = vk, IsReadOnly = readOnly, IsNa = na };
            if (!na) f.SetInitialValue(original);
            return f;
        }

        private static FixtureTypeSpec Page(string tm, PageKind kind, params SpecField[] fields) =>
            new FixtureTypeSpec(tm, kind, fields);

        private static WorkbookRow Row(string tm, params (string label, string val)[] cells)
        {
            var r = new WorkbookRow { TypeMark = tm };
            foreach (var (l, v) in cells) r.Cells[l] = v;
            return r;
        }

        private static WorkbookSnapshot Snap(PageKind kind, params WorkbookRow[] rows) =>
            new WorkbookSnapshot { Sheets = { new WorkbookSheet { Kind = kind, Rows = rows.ToList() } } };

        private static (List<SpecWriteRequest> reqs, SyncReport report) Run(
            WorkbookSnapshot snap, params FixtureTypeSpec[] model) =>
            ScheduleSyncPlanner.Plan(snap, model);

        private static SpecFieldWrite Only(List<SpecWriteRequest> reqs)
        {
            var req = Assert.Single(reqs);
            return Assert.Single(req.Fields);
        }

        // ── numeric ──

        [Fact]
        public void Numeric_Unchanged_ProducesNoWrite()
        {
            var model = Page("F1", PageKind.Fixture, Field("Power", PageKind.Fixture, SpecValueKind.Numeric, "32 W"));
            var (reqs, report) = Run(Snap(PageKind.Fixture, Row("F1", ("Power", "32"))), model);

            Assert.Empty(reqs);
            Assert.Equal(0, report.ChangedFields);
        }

        [Fact]
        public void Numeric_Changed_Writes()
        {
            var model = Page("F1", PageKind.Fixture, Field("Power", PageKind.Fixture, SpecValueKind.Numeric, "32 W"));
            var (reqs, _) = Run(Snap(PageKind.Fixture, Row("F1", ("Power", "40"))), model);

            var w = Only(reqs);
            Assert.Equal("Power", w.Label);
            Assert.Equal("40", w.Value);
        }

        [Fact]
        public void Numeric_BlankCell_Skips()
        {
            var model = Page("F1", PageKind.Fixture, Field("Power", PageKind.Fixture, SpecValueKind.Numeric, "32 W"));
            var (reqs, _) = Run(Snap(PageKind.Fixture, Row("F1", ("Power", ""))), model);
            Assert.Empty(reqs);
        }

        // ── text + <clear> ──

        [Fact]
        public void Text_Clear_EmptiesString()
        {
            var model = Page("F1", PageKind.Fixture, Field("Model", PageKind.Fixture, SpecValueKind.Text, "2x4 LED"));
            var (reqs, _) = Run(Snap(PageKind.Fixture, Row("F1", ("Model", "<clear>"))), model);

            var w = Only(reqs);
            Assert.Equal("", w.Value);
        }

        [Fact]
        public void Text_ClearOnAlreadyEmpty_Skips()
        {
            var model = Page("F1", PageKind.Fixture, Field("Model", PageKind.Fixture, SpecValueKind.Text, ""));
            var (reqs, _) = Run(Snap(PageKind.Fixture, Row("F1", ("Model", "<clear>"))), model);
            Assert.Empty(reqs);
        }

        [Fact]
        public void Text_Changed_Writes()
        {
            var model = Page("F1", PageKind.Fixture, Field("Model", PageKind.Fixture, SpecValueKind.Text, "2x4 LED"));
            var (reqs, _) = Run(Snap(PageKind.Fixture, Row("F1", ("Model", "2x2 LED"))), model);
            Assert.Equal("2x2 LED", Only(reqs).Value);
        }

        [Fact]
        public void Text_BlankCell_Skips_DoesNotClear()
        {
            var model = Page("F1", PageKind.Fixture, Field("Model", PageKind.Fixture, SpecValueKind.Text, "2x4 LED"));
            var (reqs, _) = Run(Snap(PageKind.Fixture, Row("F1", ("Model", "   "))), model);
            Assert.Empty(reqs);
        }

        // ── boolean ──

        [Fact]
        public void Boolean_Yes_WritesOne()
        {
            var model = Page("F1", PageKind.Fixture,
                Field("Remote Power Supply", PageKind.Fixture, SpecValueKind.Boolean, "0"));
            var (reqs, _) = Run(Snap(PageKind.Fixture, Row("F1", ("Remote Power Supply", "Yes"))), model);
            Assert.Equal("1", Only(reqs).Value);
        }

        [Fact]
        public void Boolean_Unchanged_Skips()
        {
            var model = Page("F1", PageKind.Fixture,
                Field("Remote Power Supply", PageKind.Fixture, SpecValueKind.Boolean, "1"));
            var (reqs, _) = Run(Snap(PageKind.Fixture, Row("F1", ("Remote Power Supply", "yes"))), model);
            Assert.Empty(reqs);
        }

        [Fact]
        public void Boolean_Unrecognized_Skipped_NotWritten()
        {
            var model = Page("F1", PageKind.Fixture,
                Field("Remote Power Supply", PageKind.Fixture, SpecValueKind.Boolean, "0"));
            var (reqs, report) = Run(Snap(PageKind.Fixture, Row("F1", ("Remote Power Supply", "maybe"))), model);

            Assert.Empty(reqs);
            Assert.Single(report.Skipped);
        }

        // ── matching / structural ──

        [Fact]
        public void UnmatchedTypeMark_SilentlyIgnored()
        {
            // A workbook row for a type not in the model writes nothing and is NOT reported —
            // removals are surfaced by the workbook flag/purge, not the sync report.
            var model = Page("F1", PageKind.Fixture, Field("Power", PageKind.Fixture, SpecValueKind.Numeric, "32 W"));
            var (reqs, report) = Run(Snap(PageKind.Fixture, Row("ZZZ", ("Power", "40"))), model);

            Assert.Empty(reqs);
            Assert.False(report.HasIssues);
        }

        [Fact]
        public void DuplicateTypeMark_Blocks_NoWrites()
        {
            var model = Page("F1", PageKind.Fixture, Field("Power", PageKind.Fixture, SpecValueKind.Numeric, "32 W"));
            var (reqs, report) = Run(Snap(PageKind.Fixture,
                Row("F1", ("Power", "40")),
                Row("F1", ("Power", "50"))), model);

            Assert.True(report.Blocking);
            Assert.Empty(reqs);
            Assert.NotEmpty(report.Errors);
        }

        [Fact]
        public void ReadOnlyField_Overridden_ReportedNotWritten()
        {
            // The designer bypassed the lock and changed a read-only cell → reported, never written.
            var model = Page("F1", PageKind.Fixture,
                Field("Power", PageKind.Fixture, SpecValueKind.Numeric, "32 W", readOnly: true));
            var (reqs, report) = Run(Snap(PageKind.Fixture, Row("F1", ("Power", "40"))), model);

            Assert.Empty(reqs);
            Assert.Single(report.Skipped);
        }

        [Fact]
        public void ReadOnlyField_Unchanged_NotReported()
        {
            // The seeded read-only value left as-is is noise — silent.
            var model = Page("F1", PageKind.Fixture,
                Field("Power", PageKind.Fixture, SpecValueKind.Numeric, "32 W", readOnly: true));
            var (reqs, report) = Run(Snap(PageKind.Fixture, Row("F1", ("Power", "32"))), model);

            Assert.Empty(reqs);
            Assert.False(report.HasIssues);
        }

        [Fact]
        public void NaField_Overridden_ReportedNotWritten()
        {
            var model = Page("F1", PageKind.Fixture,
                Field("Power", PageKind.Fixture, SpecValueKind.Numeric, "", na: true));
            var (reqs, report) = Run(Snap(PageKind.Fixture, Row("F1", ("Power", "40"))), model);

            Assert.Empty(reqs);
            Assert.Single(report.Skipped);
        }

        [Fact]
        public void NaField_EmptyCell_NotReported()
        {
            // The common case: an empty greyed n/a cell on every synced type — must be silent.
            var model = Page("F1", PageKind.Fixture,
                Field("Power", PageKind.Fixture, SpecValueKind.Numeric, "", na: true));
            var (reqs, report) = Run(Snap(PageKind.Fixture, Row("F1", ("Power", ""))), model);

            Assert.Empty(reqs);
            Assert.False(report.HasIssues);
        }

        [Fact]
        public void UnknownHeader_SilentlyIgnored()
        {
            var model = Page("F1", PageKind.Fixture, Field("Power", PageKind.Fixture, SpecValueKind.Numeric, "32 W"));
            var (reqs, report) = Run(Snap(PageKind.Fixture, Row("F1", ("Nonsense Column", "x"))), model);

            Assert.Empty(reqs);
            Assert.False(report.HasIssues);
        }

        // ── catalog warn-still-write ──

        [Fact]
        public void CatalogNumber_BadToken_Warns_ButStillWrites()
        {
            var model = Page("F1", PageKind.Fixture,
                Field("Catalog #1", PageKind.Fixture, SpecValueKind.Text, "ABC"));
            var (reqs, report) = Run(Snap(PageKind.Fixture, Row("F1", ("Catalog #1", "ABC-{xxBOGUS}"))), model);

            Assert.Equal("ABC-{xxBOGUS}", Only(reqs).Value); // written
            Assert.Single(report.Warnings);                  // and warned
        }

        [Fact]
        public void CatalogQty_BadToken_Warns_ButStillWrites()
        {
            var model = Page("F1", PageKind.Fixture,
                Field("Qty 1", PageKind.Fixture, SpecValueKind.Text, "1"));
            var (reqs, report) = Run(Snap(PageKind.Fixture, Row("F1", ("Qty 1", "bogus"))), model);

            Assert.Equal("bogus", Only(reqs).Value);
            Assert.Single(report.Warnings);
        }

        // ── driver sheet routes to driver model, and fixture cells don't cross over ──

        [Fact]
        public void DriverSheet_MatchesDriverKind_Only()
        {
            var fixture = Page("F1", PageKind.Fixture, Field("Power", PageKind.Fixture, SpecValueKind.Numeric, "32 W"));
            var driver = Page("D1", PageKind.Driver, Field("Power", PageKind.Driver, SpecValueKind.Numeric, "60 W"));

            var snap = new WorkbookSnapshot
            {
                Sheets =
                {
                    new WorkbookSheet { Kind = PageKind.Driver, Rows = { Row("D1", ("Power", "75")) } }
                }
            };
            var (reqs, _) = ScheduleSyncPlanner.Plan(snap, new[] { fixture, driver });

            var req = Assert.Single(reqs);
            Assert.Equal(PageKind.Driver, req.Kind);
            Assert.Equal("75", Assert.Single(req.Fields).Value);
        }
    }
}
