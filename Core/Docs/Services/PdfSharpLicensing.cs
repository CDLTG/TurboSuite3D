using PdfSharp;

namespace TurboSuite.Docs.Services;

/// <summary>
/// Selects PDFsharp's license. From 6.5 on, every <c>PdfDocument</c> constructor throws
/// <c>PdfSharpLicenseException</c> ("The loaded license is not valid.") until one is chosen, even
/// when none was ever loaded. TurboSuite is GPLv3 and uses PDFsharp under the AGPL option; GPLv3
/// section 13 permits that combination.
/// <para>
/// Selected lazily rather than in <c>OnStartup</c> so that a PDFsharp load failure can only break a
/// PDF export, never Revit startup. Anything that constructs a <c>PdfDocument</c> outside a PDF
/// service must call this first, because a service's static constructor has not run yet at that
/// point (see <c>PowerSuppliesViewModel</c>).
/// </para>
/// </summary>
public static class PdfSharpLicensing
{
    private static readonly object Gate = new();
    private static volatile bool _selected;

    public static void EnsureSelected()
    {
        if (_selected) return;
        lock (Gate)
        {
            if (_selected) return;
            PdfSharpLicense.SetAgplLicense();
            _selected = true;
        }
    }
}
