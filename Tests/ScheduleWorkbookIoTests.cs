using System;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using TurboSuite.Schedule.Models;
using TurboSuite.Schedule.Services;
using Xunit;

namespace TurboSuite.Tests.Schedule
{
    /// <summary>
    /// End-to-end ClosedXML round-trip: seed a workbook from model pages, read it back, and assert the
    /// data/meta contract plus the two add-only invariants (append-only, existing cells untouched, removed
    /// types flagged not deleted) and the read-safety of n/a / ⟨varies⟩ cells.
    /// </summary>
    public class ScheduleWorkbookIoTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "ts_sched_" + Guid.NewGuid().ToString("N") + ".xlsx");
        public void Dispose() { try { if (File.Exists(_path)) File.Delete(_path); } catch { } }

        // ── builders ──

        private static FieldDef Def(string label, PageKind kind) =>
            FieldDef.Roster.First(d => d.Label == label && d.AppliesTo(kind));

        private static SpecField Field(string label, PageKind kind, SpecValueKind vk, string original,
            bool varies = false, bool na = false, bool readOnly = false)
        {
            var f = new SpecField(Def(label, kind)) { ValueKind = vk, IsNa = na, IsReadOnly = readOnly };
            if (na) return f;
            if (varies) { f.IsVaries = true; return f; }
            f.SetInitialValue(original);
            return f;
        }

        private static FixtureTypeSpec Page(string tm, PageKind kind, params SpecField[] fields) =>
            new FixtureTypeSpec(tm, kind, fields);

        private static WorkbookMeta Meta() => new WorkbookMeta
        {
            ProjectPath = @"C:\jobs\Acme.rvt",
            RevitVersion = "2025",
            LastUpdated = "2026-08-24T10:00:00",
        };

        private WorkbookRow FixtureRow(WorkbookSnapshot snap, string tm) =>
            snap.Sheets.Single(s => s.Kind == PageKind.Fixture).Rows.Single(r => r.TypeMark == tm);

        // ── round-trip ──

        [Fact]
        public void WriteThenRead_RoundTripsValuesAndMeta()
        {
            var f1 = Page("F1", PageKind.Fixture,
                Field("Model", PageKind.Fixture, SpecValueKind.Text, "2x4 LED"),
                Field("Power", PageKind.Fixture, SpecValueKind.Numeric, "32 W"),
                Field("Remote Power Supply", PageKind.Fixture, SpecValueKind.Boolean, "1"));

            ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1 }, Meta());
            var snap = ScheduleWorkbookIo.Read(_path);

            Assert.Equal(@"C:\jobs\Acme.rvt", snap.Meta.ProjectPath);
            Assert.Equal("2025", snap.Meta.RevitVersion);
            Assert.Equal("2026-08-24T10:00:00", snap.Meta.LastUpdated);

            var row = FixtureRow(snap, "F1");
            Assert.Equal("2x4 LED", row.Cells["Model"]);
            Assert.Equal("32", row.Cells["Power"]);            // numeric seeded bare
            Assert.Equal("Yes", row.Cells["Remote Power Supply"]); // boolean seeded Yes/No
        }

        [Fact]
        public void NaAndVariesCells_SeededEmpty_ForReadSafety()
        {
            var f1 = Page("F1", PageKind.Fixture,
                Field("Power", PageKind.Fixture, SpecValueKind.Numeric, "", varies: true),
                Field("Voltage", PageKind.Fixture, SpecValueKind.Numeric, "", na: true));

            ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1 }, Meta());
            var row = FixtureRow(ScheduleWorkbookIo.Read(_path), "F1");

            Assert.Equal("", row.Cells["Power"].Trim());
            Assert.Equal("", row.Cells["Voltage"].Trim());
        }

        // ── add-only ──

        [Fact]
        public void SecondRun_AppendsOnlyNewTypeMarks()
        {
            var f1 = Page("F1", PageKind.Fixture, Field("Model", PageKind.Fixture, SpecValueKind.Text, "2x4"));
            ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1 }, Meta());

            var f2 = Page("F2", PageKind.Fixture, Field("Model", PageKind.Fixture, SpecValueKind.Text, "6in"));
            var result = ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1, f2 }, Meta());

            Assert.Contains(result.Added, a => a.StartsWith("F2"));
            Assert.DoesNotContain(result.Added, a => a.StartsWith("F1"));

            var marks = ScheduleWorkbookIo.Read(_path).Sheets.Single(s => s.Kind == PageKind.Fixture)
                .Rows.Select(r => r.TypeMark).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "F1", "F2" }, marks);
        }

        [Fact]
        public void SecondRun_LeavesExistingDesignerCellsUntouched()
        {
            var f1 = Page("F1", PageKind.Fixture, Field("Model", PageKind.Fixture, SpecValueKind.Text, "2x4"));
            ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1 }, Meta());

            // Simulate a designer editing the Model cell in Excel.
            EditCell("Fixtures", "F1", "Model", "DESIGNER EDIT");

            // Re-run Update with a model whose F1.Model is still the original "2x4".
            ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1 }, Meta());

            var row = FixtureRow(ScheduleWorkbookIo.Read(_path), "F1");
            Assert.Equal("DESIGNER EDIT", row.Cells["Model"]); // add-only never overwrote it
        }

        [Fact]
        public void RemovedTypeMark_Flagged_NotDeleted()
        {
            var f1 = Page("F1", PageKind.Fixture, Field("Model", PageKind.Fixture, SpecValueKind.Text, "2x4"));
            var f2 = Page("F2", PageKind.Fixture, Field("Model", PageKind.Fixture, SpecValueKind.Text, "6in"));
            ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1, f2 }, Meta());

            var result = ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1 }, Meta()); // F2 gone from model

            Assert.Contains(result.Flagged, x => x.StartsWith("F2"));
            var marks = ScheduleWorkbookIo.Read(_path).Sheets.Single(s => s.Kind == PageKind.Fixture)
                .Rows.Select(r => r.TypeMark).ToList();
            Assert.Contains("F2", marks); // flagged, still present
        }

        [Fact]
        public void StillMissingRedRow_PurgedOnNextUpdate()
        {
            var f1 = Page("F1", PageKind.Fixture, Field("Model", PageKind.Fixture, SpecValueKind.Text, "2x4"));
            var f2 = Page("F2", PageKind.Fixture, Field("Model", PageKind.Fixture, SpecValueKind.Text, "6in"));

            ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1, f2 }, Meta());
            var cycle1 = ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1 }, Meta()); // F2 gone → red (grace)
            Assert.Contains(cycle1.Flagged, x => x.StartsWith("F2"));
            Assert.Empty(cycle1.Purged);

            var cycle2 = ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1 }, Meta()); // still gone → purged
            Assert.Contains(cycle2.Purged, x => x.StartsWith("F2"));

            var marks = ScheduleWorkbookIo.Read(_path).Sheets.Single(s => s.Kind == PageKind.Fixture)
                .Rows.Select(r => r.TypeMark).ToList();
            Assert.DoesNotContain("F2", marks); // row gone; F1 survives
            Assert.Contains("F1", marks);
        }

        [Fact]
        public void PurgeLeavesOtherRowsIntact()
        {
            var f1 = Page("F1", PageKind.Fixture, Field("Model", PageKind.Fixture, SpecValueKind.Text, "keep-me"));
            var f2 = Page("F2", PageKind.Fixture, Field("Model", PageKind.Fixture, SpecValueKind.Text, "6in"));

            ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1, f2 }, Meta());
            ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1 }, Meta()); // F2 red
            ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1 }, Meta()); // F2 purged

            var row = FixtureRow(ScheduleWorkbookIo.Read(_path), "F1");
            Assert.Equal("keep-me", row.Cells["Model"]); // survivor's cells untouched by the delete
        }

        [Fact]
        public void ReturnedTypeMark_ClearsStaleFlag()
        {
            var f1 = Page("F1", PageKind.Fixture, Field("Model", PageKind.Fixture, SpecValueKind.Text, "2x4"));
            var f2 = Page("F2", PageKind.Fixture, Field("Model", PageKind.Fixture, SpecValueKind.Text, "6in"));

            ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1, f2 }, Meta());
            ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1 }, Meta());            // F2 gone → flagged red
            Assert.True(IsFlaggedRed("Fixtures", "F2"));

            var back = ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1, f2 }, Meta()); // F2 returns

            Assert.DoesNotContain(back.Flagged, x => x.StartsWith("F2")); // re-evaluated, not re-flagged
            Assert.False(IsFlaggedRed("Fixtures", "F2"));                 // stale red cleared
        }

        [Fact]
        public void RemotePowerSupply_HeaderShowsRps_ButStillRoundTrips()
        {
            var f1 = Page("F1", PageKind.Fixture,
                Field("Remote Power Supply", PageKind.Fixture, SpecValueKind.Boolean, "1"));
            ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1 }, Meta());

            using (var wb = new XLWorkbook(_path))
            {
                var ws = wb.Worksheet("Fixtures");
                int last = ws.LastColumnUsed()!.ColumnNumber();
                var headers = Enumerable.Range(1, last).Select(c => ws.Cell(2, c).GetString().Trim()).ToList();
                Assert.Contains("RPS", headers);                       // displayed alias
                Assert.DoesNotContain("Remote Power Supply", headers);  // full name not shown
            }

            // Read normalizes "RPS" back to the canonical label, so matching/round-trip is unaffected.
            var row = FixtureRow(ScheduleWorkbookIo.Read(_path), "F1");
            Assert.Equal("Yes", row.Cells["Remote Power Supply"]);
        }

        [Fact]
        public void ColumnWidths_MatchPolishSpec()
        {
            var f1 = Page("F1", PageKind.Fixture,
                Field("Catalog #1", PageKind.Fixture, SpecValueKind.Text, "A"),
                Field("Qty 1", PageKind.Fixture, SpecValueKind.Text, "1"),
                Field("Power", PageKind.Fixture, SpecValueKind.Numeric, "32 W"),
                Field("Mounting", PageKind.Fixture, SpecValueKind.Text, "Recessed"),
                Field("Remote Power Supply", PageKind.Fixture, SpecValueKind.Boolean, "1"));
            ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1 }, Meta());

            using var wb = new XLWorkbook(_path);
            var ws = wb.Worksheet("Fixtures");
            double W(string header)
            {
                int last = ws.LastColumnUsed()!.ColumnNumber();
                for (int c = 1; c <= last; c++)
                    if (ws.Cell(2, c).GetString().Trim() == header) return ws.Column(c).Width;
                return -1;
            }
            Assert.Equal(18, W("Catalog #1"));
            Assert.Equal(10, W("Qty 1"));
            Assert.Equal(16, W("Power"));      // Electrical
            Assert.Equal(16, W("Mounting"));   // Mechanical
            Assert.Equal(8, W("RPS"));         // Remote Power Supply, narrowed
        }

        [Fact]
        public void DriverAndFixture_LandOnSeparateSheets()
        {
            var f1 = Page("F1", PageKind.Fixture, Field("Model", PageKind.Fixture, SpecValueKind.Text, "2x4"));
            var d1 = Page("D1", PageKind.Driver, Field("Model", PageKind.Driver, SpecValueKind.Text, "PS-60"));
            ScheduleWorkbookIo.WriteAddOnly(_path, new[] { f1, d1 }, Meta());

            var snap = ScheduleWorkbookIo.Read(_path);
            Assert.Equal("F1", snap.Sheets.Single(s => s.Kind == PageKind.Fixture).Rows.Single().TypeMark);
            Assert.Equal("D1", snap.Sheets.Single(s => s.Kind == PageKind.Driver).Rows.Single().TypeMark);
        }

        // ── helper: reach into the .xlsx like a designer would ──

        // True when the Type Mark key cell carries the removed-type red fill (#FFC7CE).
        private bool IsFlaggedRed(string sheet, string typeMark)
        {
            using var wb = new XLWorkbook(_path);
            var ws = wb.Worksheet(sheet);
            int tmCol = -1, lastCol = ws.LastColumnUsed()!.ColumnNumber();
            for (int c = 1; c <= lastCol; c++)
                if (ws.Cell(2, c).GetString().Trim() == "Type Mark") { tmCol = c; break; }

            int lastRow = ws.LastRowUsed()!.RowNumber();
            for (int r = 4; r <= lastRow; r++)
            {
                if (ws.Cell(r, tmCol).GetString().Trim() != typeMark) continue;
                var col = ws.Cell(r, tmCol).Style.Fill.BackgroundColor.Color;
                return col.R == 0xFF && col.G == 0xC7 && col.B == 0xCE;
            }
            return false;
        }

        private void EditCell(string sheet, string typeMark, string label, string value)
        {
            using var wb = new XLWorkbook(_path);
            var ws = wb.Worksheet(sheet);
            int labelCol = -1, tmCol = -1;
            int lastCol = ws.LastColumnUsed()!.ColumnNumber();
            for (int c = 1; c <= lastCol; c++)
            {
                var h = ws.Cell(2, c).GetString().Trim();
                if (h == "Type Mark") tmCol = c;
                if (h == label) labelCol = c;
            }
            int lastRow = ws.LastRowUsed()!.RowNumber();
            for (int r = 4; r <= lastRow; r++)
            {
                if (ws.Cell(r, tmCol).GetString().Trim() == typeMark)
                {
                    ws.Cell(r, labelCol).Value = value;
                    break;
                }
            }
            wb.Save();
        }
    }
}
