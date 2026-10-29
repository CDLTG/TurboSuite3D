using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using ClosedXML.Excel;
using TurboSuite.Docs.Models;

namespace TurboSuite.Docs.Services;

/// <summary>
/// Excel export of the By-Circuit Load Schedule: one flat sheet, the same seven columns as the
/// letter PDF (<see cref="LoadsPdfService"/>), rows in the order the caller passes (the VM's
/// By-Circuit sort). A working table rather than a branded deliverable — no logo, footer, or title
/// block — so a bold frozen header row with an autofilter sits directly over the data.
///
/// <para><b>Cell types are explicit.</b> Every column is written as text except Watts, which is the
/// rounded number under a <c>0 "W"</c> format — it reads like the PDF's "450 W" yet still sums and
/// sorts. Circuit is also formatted <c>@</c> so a number like <c>1-12</c> stays text when re-typed
/// in Excel instead of becoming a date. Qty is a number for point-only circuits (a bare count — as
/// text it trips Excel's "Number Stored as Text" flag) and the display string otherwise, since
/// linear and mixed quantities aren't numbers (<c>34.5'</c>, <c>12 + 34.5'</c>). The whole Qty column
/// is left-aligned so the two kinds line up.</para>
///
/// <para><b>Circuit stays text without the green triangles.</b> A text <c>1</c> or <c>12</c> trips the
/// same "Number Stored as Text" flag, and coercing it to a number would split the column into number
/// and text halves for sort/filter. Instead the saved package is patched with an
/// <c>&lt;ignoredErrors&gt;</c> entry over the Circuit data range — ClosedXML has no API for it, so
/// this mirrors the post-save XML patching <see cref="CountsWorkbookService"/> does.</para>
/// </summary>
public static class LoadsWorkbookService
{
    private const string SheetName = "Load Schedule";

    // Mirrors LoadsPdfService.Headers so the workbook and the PDF read the same.
    private static readonly string[] Headers =
        { "Ckt", "Load", "Dimming", "Fixtures", "Qty", "Supply", "Watts" };

    private const int ColCircuit = 1;
    private const int ColQty = 5;
    private const int ColWatts = 7;

    private static readonly XNamespace Sml = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    // CT_Worksheet children that must follow <ignoredErrors> (schema order); it goes before the
    // first one present, else at the end.
    private static readonly string[] IgnoredErrorsFollowers =
    {
        "smartTags", "drawing", "legacyDrawing", "legacyDrawingHF", "picture",
        "oleObjects", "controls", "webPublishItems", "tableParts", "extLst",
    };

    // Rendered fully in memory first, so a failure never leaves a truncated file behind.
    public static void Generate(List<LoadsCircuitModel> circuits, string projectName, string outputPath) =>
        File.WriteAllBytes(outputPath, Render(circuits, projectName));

    internal static byte[] Render(IReadOnlyList<LoadsCircuitModel> circuits, string projectName)
    {
        using var buffer = new MemoryStream();
        using (var wb = Build(circuits, projectName))
            wb.SaveAs(buffer);

        if (circuits.Count > 0)
            IgnoreNumberStoredAsText(buffer, $"A2:A{circuits.Count + 1}");

        return buffer.ToArray();
    }

    private static void IgnoreNumberStoredAsText(MemoryStream package, string sqref)
    {
        package.Position = 0;
        using var archive = new ZipArchive(package, ZipArchiveMode.Update, leaveOpen: true);

        // Single-sheet workbook: its one worksheet part (the _rels/*.rels sibling doesn't match).
        var entry = archive.Entries.Single(e =>
            e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal) &&
            e.FullName.EndsWith(".xml", StringComparison.Ordinal));
        string entryPath = entry.FullName;

        XDocument sheet;
        using (var s = entry.Open())
            sheet = XDocument.Load(s);

        var ignored = new XElement(Sml + "ignoredErrors",
            new XElement(Sml + "ignoredError",
                new XAttribute("sqref", sqref),
                new XAttribute("numberStoredAsText", "1")));

        var root = sheet.Root!;
        var follower = root.Elements().FirstOrDefault(e =>
            e.Name.Namespace == Sml && IgnoredErrorsFollowers.Contains(e.Name.LocalName));
        if (follower != null) follower.AddBeforeSelf(ignored);
        else root.Add(ignored);

        entry.Delete();
        using var s2 = archive.CreateEntry(entryPath).Open();
        sheet.Save(s2);
    }

    internal static XLWorkbook Build(IReadOnlyList<LoadsCircuitModel> circuits, string projectName)
    {
        var wb = new XLWorkbook();
        wb.Properties.Title = $"{projectName} Load Schedule";
        var ws = wb.Worksheets.Add(SheetName);

        for (int c = 0; c < Headers.Length; c++)
            ws.Cell(1, c + 1).Value = Headers[c];

        var header = ws.Range(1, 1, 1, Headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        int row = 2;
        foreach (var circuit in circuits)
        {
            ws.Cell(row, ColCircuit).Value = circuit.CircuitNumber;
            ws.Cell(row, 2).Value = circuit.LoadName;
            ws.Cell(row, 3).Value = circuit.DimmingProtocol;
            ws.Cell(row, 4).Value = circuit.FixturesDisplay;
            if (IsPointOnly(circuit))
                ws.Cell(row, ColQty).Value = circuit.FixtureGroups.Sum(g => g.Quantity);
            else
                ws.Cell(row, ColQty).Value = circuit.QuantityDisplay;
            ws.Cell(row, 6).Value = circuit.DriverDisplay;
            ws.Cell(row, ColWatts).Value = Math.Round(circuit.ApparentLoadVA);
            row++;
        }

        int lastRow = row - 1;
        if (lastRow >= 2)
        {
            ws.Range(2, ColCircuit, lastRow, ColCircuit).Style.NumberFormat.Format = "@";
            ws.Range(2, ColQty, lastRow, ColQty).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            ws.Range(2, ColWatts, lastRow, ColWatts).Style.NumberFormat.Format = "0 \"W\"";
        }

        ws.Range(1, 1, lastRow, Headers.Length).SetAutoFilter();
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, Headers.Length).AdjustToContents();

        return wb;
    }

    // The case where QuantityDisplay is a bare integer — the same sum, written as a number.
    private static bool IsPointOnly(LoadsCircuitModel circuit) =>
        circuit.FixtureGroups.Count > 0 && circuit.FixtureGroups.All(g => !g.IsLinear);
}
