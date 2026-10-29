using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using ClosedXML.Excel;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using Xunit;

namespace TurboSuite.Tests.Docs
{
    /// <summary>
    /// Oracle suite for <see cref="LoadsWorkbookService"/>: the Excel (By Circuit) Load Schedule.
    /// Pins the PDF-matching header, caller row order, the text/number cell split (circuit numbers
    /// never coerced, Watts a real number), and the frozen/filtered header row. Round-trips through
    /// a saved stream so what's asserted is what Excel would open.
    /// </summary>
    public class LoadsWorkbookServiceTests
    {
        private static LoadsCircuitModel Ckt(string number, string load, double va) =>
            new LoadsCircuitModel { CircuitNumber = number, LoadName = load, ApparentLoadVA = va };

        // Through Render — the exact bytes Generate writes, post-save patch included.
        private static IXLWorksheet RoundTrip(List<LoadsCircuitModel> circuits) =>
            new XLWorkbook(new MemoryStream(LoadsWorkbookService.Render(circuits, "Proj"))).Worksheet(1);

        private static XDocument SheetXml(List<LoadsCircuitModel> circuits)
        {
            using var zip = new ZipArchive(new MemoryStream(LoadsWorkbookService.Render(circuits, "Proj")));
            using var s = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            return XDocument.Load(s);
        }

        private static readonly XNamespace Sml = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        [Fact]
        public void Header_MatchesPdfColumns()
        {
            var ws = RoundTrip(new List<LoadsCircuitModel> { Ckt("1", "Kitchen", 100) });

            var headers = new List<string>();
            for (int c = 1; c <= 7; c++) headers.Add(ws.Cell(1, c).GetString());

            Assert.Equal(new[] { "Ckt", "Load", "Dimming", "Fixtures", "Qty", "Supply", "Watts" }, headers);
            Assert.Equal("Load Schedule", ws.Name);
        }

        [Fact]
        public void Rows_KeepCallerOrder()
        {
            var ws = RoundTrip(new List<LoadsCircuitModel>
            {
                Ckt("2", "B", 0), Ckt("10", "C", 0), Ckt("<...>", "D", 0),
            });

            Assert.Equal("2", ws.Cell(2, 1).GetString());
            Assert.Equal("10", ws.Cell(3, 1).GetString());
            Assert.Equal("<...>", ws.Cell(4, 1).GetString());
        }

        [Fact]
        public void CircuitNumber_StaysText_EvenWhenDateOrNumberShaped()
        {
            var ws = RoundTrip(new List<LoadsCircuitModel> { Ckt("1-12", "A", 0), Ckt("007", "B", 0) });

            Assert.Equal(XLDataType.Text, ws.Cell(2, 1).DataType);
            Assert.Equal("1-12", ws.Cell(2, 1).GetString());
            Assert.Equal(XLDataType.Text, ws.Cell(3, 1).DataType);
            Assert.Equal("007", ws.Cell(3, 1).GetString());
        }

        [Fact]
        public void Watts_IsRoundedNumber_WithUnitFormat()
        {
            var ws = RoundTrip(new List<LoadsCircuitModel> { Ckt("1", "A", 449.6) });

            var cell = ws.Cell(2, 7);
            Assert.Equal(XLDataType.Number, cell.DataType);
            Assert.Equal(450d, cell.GetDouble());
            Assert.Equal("0 \"W\"", cell.Style.NumberFormat.Format);
        }

        private static LoadsFixtureGroup Point(string mark, int qty) =>
            new LoadsFixtureGroup { TypeMark = mark, Quantity = qty };

        private static LoadsFixtureGroup Linear(string mark, double feet) =>
            new LoadsFixtureGroup { TypeMark = mark, IsLinear = true, TotalLinearLengthFeet = feet };

        [Fact]
        public void Qty_PointOnly_IsNumber()
        {
            var c = Ckt("1", "A", 0);
            c.FixtureGroups = new List<LoadsFixtureGroup> { Point("A1", 3), Point("A2", 9) };

            var cell = RoundTrip(new List<LoadsCircuitModel> { c }).Cell(2, 5);

            Assert.Equal(XLDataType.Number, cell.DataType);
            Assert.Equal(12d, cell.GetDouble());
        }

        [Fact]
        public void Qty_LinearOrMixed_StaysDisplayText()
        {
            var linear = Ckt("1", "A", 0);
            linear.FixtureGroups = new List<LoadsFixtureGroup> { Linear("T1", 34.5) };
            var mixed = Ckt("2", "B", 0);
            mixed.FixtureGroups = new List<LoadsFixtureGroup> { Point("A1", 12), Linear("T1", 34.5) };

            var ws = RoundTrip(new List<LoadsCircuitModel> { linear, mixed });

            Assert.Equal(XLDataType.Text, ws.Cell(2, 5).DataType);
            Assert.Equal(linear.QuantityDisplay, ws.Cell(2, 5).GetString());
            Assert.Equal(XLDataType.Text, ws.Cell(3, 5).DataType);
            Assert.Equal(mixed.QuantityDisplay, ws.Cell(3, 5).GetString());
        }

        [Fact]
        public void Qty_NumbersAndLengths_AllLeftAligned()
        {
            var point = Ckt("1", "A", 0);
            point.FixtureGroups = new List<LoadsFixtureGroup> { Point("A1", 3) };
            var linear = Ckt("2", "B", 0);
            linear.FixtureGroups = new List<LoadsFixtureGroup> { Linear("T1", 8.9) };

            var ws = RoundTrip(new List<LoadsCircuitModel> { point, linear });

            Assert.Equal(XLAlignmentHorizontalValues.Left, ws.Cell(2, 5).Style.Alignment.Horizontal);
            Assert.Equal(XLAlignmentHorizontalValues.Left, ws.Cell(3, 5).Style.Alignment.Horizontal);
        }

        [Fact]
        public void CircuitColumn_IgnoresNumberStoredAsText_OverDataRange()
        {
            var xml = SheetXml(new List<LoadsCircuitModel> { Ckt("1", "A", 0), Ckt("2", "B", 0), Ckt("3", "C", 0) });

            var ignored = Assert.Single(xml.Root!.Elements(Sml + "ignoredErrors").Elements(Sml + "ignoredError"));
            Assert.Equal("A2:A4", (string?)ignored.Attribute("sqref"));
            Assert.Equal("1", (string?)ignored.Attribute("numberStoredAsText"));
        }

        [Fact]
        public void IgnoredErrors_SitsInSchemaOrder()
        {
            // Must follow pageMargins/pageSetup/headerFooter and precede drawing/tableParts/extLst,
            // or Excel reports the file as corrupt.
            var xml = SheetXml(new List<LoadsCircuitModel> { Ckt("1", "A", 0) });
            var names = xml.Root!.Elements().Select(e => e.Name.LocalName).ToList();

            int at = names.IndexOf("ignoredErrors");
            Assert.True(at > names.IndexOf("sheetData"));
            foreach (var before in new[] { "autoFilter", "pageMargins", "pageSetup", "headerFooter" })
                if (names.Contains(before)) Assert.True(names.IndexOf(before) < at, before);
            foreach (var after in new[] { "drawing", "legacyDrawing", "tableParts", "extLst" })
                if (names.Contains(after)) Assert.True(names.IndexOf(after) > at, after);
        }

        [Fact]
        public void Qty_NoFixtures_IsBlank()
        {
            var ws = RoundTrip(new List<LoadsCircuitModel> { Ckt("1", "A", 0) });

            Assert.True(ws.Cell(2, 5).IsEmpty());
        }

        [Fact]
        public void HeaderRow_IsFrozenAndFiltered()
        {
            var ws = RoundTrip(new List<LoadsCircuitModel> { Ckt("1", "A", 0), Ckt("2", "B", 0) });

            Assert.Equal(1, ws.SheetView.SplitRow);
            Assert.True(ws.AutoFilter.IsEnabled);
            Assert.Equal("A1:G3", ws.AutoFilter.Range.RangeAddress.ToString());
        }
    }
}
