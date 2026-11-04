using System;
using System.Collections.Generic;
using System.IO;
using PdfSharp.Pdf.IO;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using Xunit;

namespace TurboSuite.Tests.Docs
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Pins the PDFsharp 6.5+ license gate. Every PdfDocument constructor throws
    //  PdfSharpLicenseException ("The loaded license is not valid.") until a license is selected, so
    //  a real PDF service must produce a real file with no setup from the caller.
    //
    //  For me (Claude): keep this the ONLY test in the assembly that touches PDFsharp. The selected
    //  license is process-wide with no public reset, so any test running first would select it and
    //  make this one pass even with the service's selection removed.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    public class PdfSharpLicensingTests
    {
        [Fact]
        public void SchedulePdfService_generates_a_readable_pdf_without_caller_setup()
        {
            string path = Path.Combine(Path.GetTempPath(), $"turbosuite-license-{Guid.NewGuid():N}.pdf");
            try
            {
                var fixtures = new List<ScheduleFixtureModel>
                {
                    new() { TypeMark = "A1", Classification = "Downlight", CatalogNumber = "CAT-1", Manufacturer = "Acme" },
                };

                SchedulePdfService.Generate(fixtures, "License Test", path, useLargeFormat: false, new DocsSettings());

                using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                Assert.True(pdf.PageCount >= 1);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
