using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using Xunit;

namespace TurboSuite.Tests.Docs
{
    /// <summary>
    /// Guards the Counts Quote Client Ea./Ext. columns. ClosedXML does NOT evaluate formulas
    /// (no SUMPRODUCT/ANCHORARRAY/VSTACK/spill/LET computation), so these assert on the emitted
    /// formula STRINGS of the Worksheet Client helper spills (BT = Client Ea., BU = Client Ext.),
    /// driven through the real service via GenerateNew.
    /// </summary>
    public class CountsClientColumnTests
    {
        // A handful of Types with catalog/mfr/qty so the Worksheet has real data rows. Simple slots
        // (no length tokens / cut lists) keep the CatalogQty + length-token validators happy.
        private static List<CountsFixtureModel> SampleFixtures()
        {
            var fixtures = new List<CountsFixtureModel>();
            for (int i = 1; i <= 6; i++)
            {
                fixtures.Add(new CountsFixtureModel
                {
                    TypeMark = $"T{i}",
                    Manufacturer = $"Mfr{i}",
                    CatalogNumbers = new[] { $"CAT-{i}-A", $"CAT-{i}-B", "", "", "", "" },
                    CatalogQtys = new[] { "1", "2", "", "", "", "" },
                    Count = i,
                    Notes = new[] { $"note{i}", "", "", "", "", "" },
                });
            }
            return fixtures;
        }

        private static string GenerateToTempFile()
        {
            string outPath = Path.Combine(Path.GetTempPath(), $"counts_client_{Guid.NewGuid():N}.xlsx");
            CountsWorkbookService.GenerateNew(
                SampleFixtures(), "Probe Project", "PN-001", "Somewhere",
                outPath, repDirectoryPath: "", headerDate: DateTime.Today);
            return outPath;
        }

        /// <summary>
        /// The point of the feature: freight reaches the client at the Sell figure UNMARKED, while
        /// product + tariff + Lutron carry the client markup. Assertions key on named ranges, which
        /// render verbatim regardless of whether ClosedXML keeps the _xlfn./_xlpm. prefixes on read.
        /// This also exercises the real wiring end-to-end — it fails if the Quote pipeline call
        /// forgets clientCols, or if col 10/11 is repointed at the wrong helper.
        /// </summary>
        [Fact]
        public void ClientColumns_ExcludeFreightFromMarkup()
        {
            string outPath = GenerateToTempFile();
            try
            {
                using var wb = new XLWorkbook(outPath);
                var ws = wb.Worksheet("Worksheet");
                string bu2 = ws.Cell("BU2").FormulaA1 ?? string.Empty; // Client Ext.
                string bt2 = ws.Cell("BT2").FormulaA1 ?? string.Empty; // Client Ea.

                // Freight reaches the client UNMARKED — the whole point of the change:
                Assert.DoesNotContain("FreightSell*(1+ClientMarkup)", bu2);
                Assert.DoesNotContain("N(FreightSell)*(1+ClientMarkup)", bu2);
                Assert.Contains("FreightSell", bu2); // freight term still present in the footer

                // Product + Lutron ARE marked up (guards against over-correcting to "nothing scales"):
                Assert.Contains("LutronSubtotal*(1+ClientMarkup)", bu2);
                Assert.Contains("(1+ClientMarkup)", bu2); // subtotal / tariff still scaled

                // Client Ea. carries the markup and the sentinels:
                Assert.Contains("(1+ClientMarkup)", bt2);
                Assert.Contains("NO BID", bt2);
            }
            finally { TryDelete(outPath); }
        }

        /// <summary>
        /// Migration guard for the Counts "Do nothing" story. The Client columns reference the
        /// ClientMarkup named range, which only exists on workbooks built after commit 6091c15.
        /// A workbook built after the Dashboard shipped but before 6091c15 already has a Dashboard,
        /// so EnsureDashboardSheet early-returns and the name is NEVER backfilled on update — the
        /// rebuilt Worksheet Client helper formulas (BT/BU) then reference an undefined name. This
        /// pins the behavior the design depends on: ClosedXML 0.105.1 must NOT throw — neither at
        /// Save (serialize) nor on reopen — so the outcome stays a cosmetic #NAME? in Excel rather
        /// than a hard failure of GenerateUpdate at [stage=rebuild-contractor-sheets]. Reproduced
        /// against the REAL emitted formula shape (name deleted after generation), not a stand-in.
        /// If a ClosedXML upgrade ever makes this throw, that migration decision must be revisited
        /// (fall back to guarding the columns behind name existence).
        /// </summary>
        [Fact]
        public void UndefinedClientMarkupName_SavesWithoutThrowing()
        {
            string outPath = GenerateToTempFile();
            try
            {
                using var wb = new XLWorkbook(outPath);

                // Simulate a pre-6091c15 workbook: the real client formulas are already written,
                // now remove the name they reference.
                wb.DefinedNames.FirstOrDefault(n =>
                    string.Equals(n.Name, "ClientMarkup", StringComparison.OrdinalIgnoreCase))?.Delete();
                Assert.DoesNotContain(wb.DefinedNames, n =>
                    string.Equals(n.Name, "ClientMarkup", StringComparison.OrdinalIgnoreCase));

                // Save (serialize) must not throw on the undefined name.
                using var ms = new MemoryStream();
                wb.SaveAs(ms);

                // Reopen must not throw, and the reference round-trips intact (→ #NAME? in Excel).
                using var reopened = new XLWorkbook(ms);
                Assert.Contains("ClientMarkup",
                    reopened.Worksheet("Worksheet").Cell("BU2").FormulaA1);
            }
            finally { TryDelete(outPath); }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* best-effort temp cleanup */ }
        }
    }
}
