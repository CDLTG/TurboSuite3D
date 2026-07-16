#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Name.Services;
using TurboSuite.Shared.Services;

namespace TurboSuite.Spike;

/// <summary>
/// TurboSpike — the throwaway diagnostic bench.
///
/// PURPOSE: an always-available (in dev) ribbon button whose <see cref="Execute"/> body is meant to be
/// SWAPPED per-investigation. When you need to know something about the running model before writing
/// targeted code — what a parameter's StorageType actually is, whether an API member exists on this
/// Revit version, what a family's connectors/geometry look like — drop a probe here, build, and read
/// the dialog.
///
/// STATE: rides the shared <c>ExperimentalCommandsEnabled</c> gate in <see cref="App.TurboSuiteApplication"/>,
/// so it surfaces every dev session and is gated off in shipped builds — it never reaches production users.
///
/// Keep this ReadOnly and side-effect-free by default. If a probe needs to write, wrap it in a Transaction
/// and change the attribute for the duration of that spike — then revert. This file is scratch space; the
/// body below is only a sensible starting probe, not something to preserve.
///
/// CURRENT PROBE (TurboName-5), round 2. Round 1 established:
///   • MText.HorizontalWidth / VerticalHeight ARE populated but are CONSTANT (0.9 / 0.2) across every
///     entity regardless of content — not the DXF 42/43 measured extents. Unusable for a visual center.
///   • MText.RectangleWidth is the only property that tracks content length (20.402 for 5-char strings,
///     26.058 for a 6-char one). HYPOTHESIS: it carries the real measured width.
///   • Round 1 read the CONFIGURED room-name layer, which on the test doc pointed at the ceiling layer —
///     so it dumped 18 ceiling heights and zero room names.
/// So round 2 ignores the settings entirely and dumps EVERY TEXT/MTEXT on EVERY layer of every linked
/// CAD, grouped per (file, layer), to answer:
///   1. Which layer actually holds room names, and are multi-line labels separate entities?
///   2. Does RectangleWidth track content? The ratio RectWidth / (chars × Height) is printed per entity —
///      if it clusters around a constant (~0.6-0.8) it's a measured width and a visual center is derivable
///      from it; if it's all over the place it's a user-drawn box and the whole approach is dead.
///   3. Do room names arrive from BOTH the plan and the RCP (the SourceLinkName gap)?
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class SpikeCommand : IExternalCommand
{
    // Report each label's nearest neighbours within this radius (generous on purpose — we want to SEE
    // the inflated raw distances, not filter them out).
    private const double NeighborReportRadiusFt = 12.0;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc?.Document == null)
        {
            TaskDialog.Show("TurboSpike", "No active document.");
            return Result.Cancelled;
        }

        Document doc = uidoc.Document;
        View view = doc.ActiveView;

        var sb = new StringBuilder();
        sb.AppendLine($"Document: {doc.Title}");
        sb.AppendLine($"Revit version: {commandData.Application.Application.VersionNumber}");
        sb.AppendLine($"Active view: {view.Name}  ({view.ViewType})");
        sb.AppendLine();

        // Settings are reported for CONTEXT only — this round does not use them to pick layers.
        var settings = CadRoomSourceStorageService.Load(doc);
        if (settings == null) sb.AppendLine("CAD Room Source settings: NOT configured (context only).");
        else
        {
            sb.AppendLine($"Settings (context only — this probe dumps ALL layers):");
            sb.AppendLine($"  Mode='{settings.Mode}'  RoomNameLayer='{settings.RoomNameLayer}'  " +
                          $"CeilingHeightLayer='{settings.CeilingHeightLayer}'  SourceLinkName='{settings.SourceLinkName}'");
        }
        sb.AppendLine();

        var labels = new List<Label>();

        var cadLinks = new FilteredElementCollector(doc, view.Id)
            .OfClass(typeof(ImportInstance))
            .Cast<ImportInstance>()
            .Where(ii => ii.IsLinked)
            .ToList();

        sb.AppendLine($"Linked CADs visible in this view: {cadLinks.Count}");

        foreach (var import in cadLinks)
        {
            if (doc.GetElement(import.GetTypeId()) is not CADLinkType linkType) continue;
            var extRef = linkType.GetExternalFileReference();
            if (extRef?.GetAbsolutePath() == null) continue;

            string dwgPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetAbsolutePath());
            string fileName = Path.GetFileName(dwgPath);
            if (!File.Exists(dwgPath)) { sb.AppendLine($"  {fileName}: FILE NOT FOUND"); continue; }

            CadDocument cadDoc;
            try
            {
                using var reader = new DwgReader(dwgPath);
                cadDoc = reader.Read();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  {fileName}: READ FAILED — {ex.Message}");
                continue;
            }

            double u = UnitToFeet(cadDoc.Header.InsUnits);
            Transform xf = import.GetTransform();
            sb.AppendLine($"  {fileName}:  InsUnits={cadDoc.Header.InsUnits}  unitToFeet={u:F6}");

            foreach (var e in cadDoc.Entities)
            {
                if (e is not TextEntity && e is not MText) continue;
                var lab = Describe(e, u, xf, fileName, e.Layer?.Name ?? "");
                if (lab != null) labels.Add(lab);
            }
        }

        // ── Layer census across links: which layers exist in BOTH files? (the SourceLinkName question) ──
        sb.AppendLine();
        sb.AppendLine("═══ Layer census (TEXT/MTEXT counts per file) ═══");
        var files = labels.Select(l => l.File).Distinct().OrderBy(f => f).ToList();
        foreach (var layer in labels.Select(l => l.Layer).Distinct().OrderBy(x => x))
        {
            var perFile = files.Select(f => (f, n: labels.Count(l => l.Layer == layer && l.File == f))).ToList();
            bool inBoth = perFile.Count(t => t.n > 0) > 1;
            sb.AppendLine($"  {layer}{(inBoth ? "   <<< PRESENT IN >1 LINK — duplicate-seed hazard" : "")}");
            foreach (var (f, n) in perFile.Where(t => t.n > 0))
                sb.AppendLine($"      {n,5}  {f}");
        }

        // ── Per (file, layer) dump ──
        foreach (var grp in labels.GroupBy(l => (l.File, l.Layer)).OrderBy(g => g.Key.File).ThenBy(g => g.Key.Layer))
        {
            sb.AppendLine();
            sb.AppendLine($"═══════════════════════════════════════════════════════════════");
            sb.AppendLine($"FILE  {grp.Key.File}");
            sb.AppendLine($"LAYER {grp.Key.Layer}   ({grp.Count()} entities)");
            sb.AppendLine($"═══════════════════════════════════════════════════════════════");

            foreach (var l in grp.OrderByDescending(l => l.InsFt.Y).ThenBy(l => l.InsFt.X))
            {
                sb.AppendLine($"[{l.Kind}] '{l.Stripped}'");
                sb.AppendLine($"    ins ({l.InsFt.X:F3}, {l.InsFt.Y:F3}) ft   textHt {l.HeightFt:F3} ft   raw '{l.Raw}'");
                sb.AppendLine($"    {l.Detail}");
                if (l.WidthRatio.HasValue)
                    sb.AppendLine($"    RectWidth/(chars×Height) = {l.WidthRatio.Value:F3}   " +
                                  $"(chars={l.Stripped.Length}, RectWidth={l.RectWidth:F3}, Height={l.RawHeight:F3})");
            }

            // Nearest-neighbour distances within this layer — the multi-line-label signal.
            var items = grp.ToList();
            if (items.Count < 2) continue;
            sb.AppendLine();
            sb.AppendLine($"  ── nearest neighbours within '{grp.Key.Layer}' ──");
            foreach (var a in items.OrderByDescending(l => l.InsFt.Y).ThenBy(l => l.InsFt.X))
            {
                var near = items.Where(b => !ReferenceEquals(a, b))
                    .Select(b => (b, d: Dist(a.InsFt, b.InsFt)))
                    .Where(t => t.d <= NeighborReportRadiusFt)
                    .OrderBy(t => t.d).Take(2).ToList();
                if (near.Count == 0) continue;
                sb.AppendLine($"  '{a.Stripped}'");
                foreach (var (b, d) in near)
                    sb.AppendLine($"      → '{b.Stripped}'   dIns {d:F2} ft   " +
                                  $"(dx {Math.Abs(a.InsFt.X - b.InsFt.X):F2}, dy {Math.Abs(a.InsFt.Y - b.InsFt.Y):F2})   " +
                                  $"dy/textHt {(b.HeightFt > 1e-9 ? (Math.Abs(a.InsFt.Y - b.InsFt.Y) / b.HeightFt).ToString("F2") : "?")}");
            }
        }

        // ── RectangleWidth hypothesis: is the ratio stable? ──
        sb.AppendLine();
        sb.AppendLine("═══ RectangleWidth hypothesis ═══");
        sb.AppendLine("  If RectWidth is a MEASURED width, RectWidth/(chars×Height) clusters tightly (~0.6-0.8).");
        sb.AppendLine("  If it's a user-drawn wrap box, the ratio scatters and the visual-center approach is dead.");
        var ratios = labels.Where(l => l.WidthRatio.HasValue).Select(l => l.WidthRatio.Value).ToList();
        if (ratios.Count == 0) sb.AppendLine("  No MTEXT with usable RectangleWidth/Height.");
        else
        {
            double mean = ratios.Average();
            double sd = Math.Sqrt(ratios.Sum(r => (r - mean) * (r - mean)) / ratios.Count);
            sb.AppendLine($"  n={ratios.Count}  mean={mean:F3}  sd={sd:F3}  min={ratios.Min():F3}  max={ratios.Max():F3}");
            sb.AppendLine($"  → {(sd < 0.12 ? "TIGHT — consistent with a measured width." : "SCATTERED — looks like a user-drawn box.")}");
        }

        Finish(sb);
        return Result.Succeeded;
    }

    private static void Finish(StringBuilder sb)
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "TurboSpike_labels.txt");
        string summary;
        try
        {
            File.WriteAllText(path, sb.ToString());
            summary = $"Full dump written to:\n{path}\n\n" +
                      "Open it and paste the contents back into the session.";
        }
        catch (Exception ex)
        {
            summary = $"Could not write dump file: {ex.Message}";
        }

        var head = string.Join("\n", sb.ToString().Split('\n').Take(28));
        TaskDialog.Show("TurboSpike — TurboName-5 label geometry", summary + "\n\n──────────\n" + head);
    }

    private sealed class Label
    {
        public string Kind, Raw, Stripped, Layer, File, Detail;
        public XYZ InsFt;
        public double HeightFt, RawHeight, RectWidth;
        public double? WidthRatio;
    }

    private static double Dist(XYZ a, XYZ b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static Label Describe(Entity e, double u, Transform xf, string file, string layer)
    {
        double insX, insY, textHeight, rectWidth = 0;
        string kind, raw, detail;

        if (e is MText m)
        {
            kind = "MTEXT";
            raw = m.Value ?? "";
            insX = m.InsertPoint.X; insY = m.InsertPoint.Y;
            textHeight = m.Height;
            rectWidth = m.RectangleWidth;
            detail =
                $"AttachmentPoint={m.AttachmentPoint} ({(int)m.AttachmentPoint})  Height={m.Height:F4}  " +
                $"LineSpacing={m.LineSpacing:F4} ({m.LineSpacingStyle})  " +
                $"HorizontalWidth={m.HorizontalWidth:F4}  VerticalHeight={m.VerticalHeight:F4}  " +
                $"RectangleWidth={m.RectangleWidth:F4}  RectangleHeight={m.RectangleHeight:F4}  " +
                $"Rotation={m.Rotation:F4}  HasColumns={m.HasColumns}";
        }
        else if (e is TextEntity t)
        {
            kind = "TEXT";
            raw = t.Value ?? "";
            // For non-baseline/left justification the REAL position is AlignmentPoint, not InsertPoint.
            bool useAlign = (int)t.HorizontalAlignment != 0 || (int)t.VerticalAlignment != 0;
            insX = useAlign ? t.AlignmentPoint.X : t.InsertPoint.X;
            insY = useAlign ? t.AlignmentPoint.Y : t.InsertPoint.Y;
            textHeight = t.Height;
            detail =
                $"HorizontalAlignment={t.HorizontalAlignment} ({(int)t.HorizontalAlignment})  " +
                $"VerticalAlignment={t.VerticalAlignment} ({(int)t.VerticalAlignment})  " +
                $"Height={t.Height:F4}  WidthFactor={t.WidthFactor:F4}  Rotation={t.Rotation:F4}  " +
                $"InsertPoint=({t.InsertPoint.X:F4},{t.InsertPoint.Y:F4})  " +
                $"AlignmentPoint=({t.AlignmentPoint.X:F4},{t.AlignmentPoint.Y:F4})  usedAlignmentPoint={useAlign}  " +
                $"[no width property on TEXT]";
        }
        else return null;

        string stripped = CadRoomExtractorService.StripCadFormatting(raw);
        double? ratio = null;
        if (e is MText && rectWidth > 1e-9 && textHeight > 1e-9 && stripped.Length > 0)
            ratio = rectWidth / (stripped.Length * textHeight);

        return new Label
        {
            Kind = kind,
            Raw = raw,
            Stripped = stripped,
            Layer = layer,
            File = file,
            Detail = detail,
            InsFt = xf.OfPoint(new XYZ(insX * u, insY * u, 0)),
            HeightFt = textHeight * u,
            RawHeight = textHeight,
            RectWidth = rectWidth,
            WidthRatio = ratio,
        };
    }

    // Local copy — CadRoomExtractorService's is private, and this file is throwaway scratch that must not
    // force a visibility change on production code.
    private static double UnitToFeet(ACadSharp.Types.Units.UnitsType units) => units switch
    {
        ACadSharp.Types.Units.UnitsType.Inches => 1.0 / 12.0,
        ACadSharp.Types.Units.UnitsType.Feet => 1.0,
        ACadSharp.Types.Units.UnitsType.Millimeters => 1.0 / 304.8,
        ACadSharp.Types.Units.UnitsType.Centimeters => 1.0 / 30.48,
        ACadSharp.Types.Units.UnitsType.Meters => 1.0 / 0.3048,
        _ => 1.0 / 12.0,
    };
}
