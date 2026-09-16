using System.IO;
using ClosedXML.Excel;
using Xunit;

namespace TurboSuite.Tests.Docs
{
    /// <summary>
    /// Regression guard for the Counts "Do nothing" migration: the Client Ea./Client Ext. columns
    /// reference the `ClientMarkup` named range, which only exists on workbooks built after that
    /// feature shipped. Pre-feature workbooks are updated in place (RebuildContractorSheets), so
    /// their rebuilt Quote emits that reference against a name that doesn't resolve. This pins the
    /// behavior the design depends on: ClosedXML 0.105.1 must NOT throw on an undefined name —
    /// neither at FormulaA1 assignment (parse) nor at Save (serialize) — so the outcome stays a
    /// cosmetic #NAME? in Excel rather than a hard failure of GenerateUpdate at
    /// [stage=rebuild-contractor-sheets]. If a ClosedXML upgrade ever makes this throw, that
    /// migration decision must be revisited (fall back to guarding the columns behind name
    /// existence).
    /// </summary>
    public class ClientMarkupProbeTests
    {
        // The exact Client Ea. formula the plan assigns (no leading '=', matching BuildQuoteSheet's
        // FormulaA1 convention), referencing the undefined name ClientMarkup.
        private const string ClientEaFormula =
            "IF(ISNUMBER(_xlfn.ANCHORARRAY(Worksheet!AO2))," +
            "_xlfn.ANCHORARRAY(Worksheet!AO2)*(1+ClientMarkup)," +
            "_xlfn.ANCHORARRAY(Worksheet!AO2))";

        [Fact]
        public void UndefinedClientMarkupName_AssignsAndSaves_WithoutThrowing()
        {
            using var wb = new XLWorkbook();
            var wsSheet = wb.Worksheets.Add("Worksheet");
            wsSheet.Cell("AO2").Value = 100; // stand-in for the Sell Ea. spill anchor
            var quote = wb.Worksheets.Add("Quote");

            // Guard: the name genuinely does not exist (mirrors an old field workbook).
            Assert.DoesNotContain(wb.DefinedNames, n =>
                string.Equals(n.Name, "ClientMarkup", System.StringComparison.OrdinalIgnoreCase));

            // 1. Assignment (parse) must not throw on the undefined name.
            quote.Cell("J8").FormulaA1 = ClientEaFormula;

            // 2. Save (serialize) must not throw either.
            using var ms = new MemoryStream();
            wb.SaveAs(ms);

            // Sanity: the formula round-tripped intact.
            using var reopened = new XLWorkbook(ms);
            Assert.Contains("ClientMarkup",
                reopened.Worksheet("Quote").Cell("J8").FormulaA1);
        }
    }
}
