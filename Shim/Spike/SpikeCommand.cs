#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
// Autodesk.Revit.UI.TextBox is the ribbon control — a probe usually wants the WPF one.
using TextBox = System.Windows.Controls.TextBox;

namespace TurboSuite.Spike;

/// <summary>
/// TurboSpike — the throwaway diagnostic bench. See the class rules in CLAUDE.md; clobber freely.
///
/// CURRENT PROBE (room-detection owned-layer, round-trip toggle confirmation): the owned-layer plan is
/// Areas-mode-invariant Spaces, consumed under whatever volume-computation mode the doc is in (the owner's
/// workflow: live in Areas-only, flip to Areas-and-Volumes only for ElumTools calcs, flip back). This probe
/// confirms two things in one pass, entirely inside a single rolled-back transaction (nothing persists):
///   (1) The AO -> A+V -> AO round trip is NON-DESTRUCTIVE — every Space's name, area, and boundary
///       footprint is byte-identical before and after, so "spaces disappearing" during calcs is purely a
///       reversible volume collapse. Also times each regen so the toggle's cost on this model is known.
///   (2) GetBoundarySegments returns valid boundary loops under Areas-and-Volumes EVEN for the
///       volume-collapsed grade spaces — the footprint the finder will read is intact
///       in the calc-phase mode too.
///
/// SAFETY: read-mostly. All setting changes happen inside ONE transaction that is always RolledBack, so the
/// document's volume setting is restored regardless of its starting value. Do not save this session anyway.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SpikeCommand : IExternalCommand
{
    private sealed class Snap
    {
        public string Name;
        public string Level;
        public double LevelProjElev;
        public double Area;
        public double Volume;
        public int LoopCount;
        public int SegCount;
        public double Perimeter;
    }

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc?.Document;
        if (doc == null)
        {
            TaskDialog.Show("TurboSpike", "No active document.");
            return Result.Cancelled;
        }

        var sb = new StringBuilder();
        sb.AppendLine("TurboSpike — Space round-trip toggle (Areas-only -> Areas+Volumes -> Areas-only)");
        sb.AppendLine("Read-mostly: all toggles happen in ONE rolled-back transaction. Nothing persists.");
        sb.AppendLine("Confirms (1) the round trip preserves name/area/footprint, and (2) GetBoundarySegments");
        sb.AppendLine("returns valid loops under Areas+Volumes even for volume-collapsed spaces.");
        sb.AppendLine();

        bool startMode = AreaVolumeSettings.GetAreaVolumeSettings(doc).ComputeVolumes;
        sb.AppendLine($"Document              : {doc.Title}");
        sb.AppendLine($"Starting volume mode  : {(startMode ? "Areas and Volumes" : "Areas only")} (restored on rollback)");

        var spaceIds = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_MEPSpaces)
            .WhereElementIsNotElementType()
            .ToElementIds()
            .ToList();
        sb.AppendLine($"Spaces found          : {spaceIds.Count}");
        if (spaceIds.Count == 0)
        {
            sb.AppendLine("\r\nNo Spaces — nothing to measure.");
            ShowReport(commandData, sb.ToString());
            return Result.Succeeded;
        }
        sb.AppendLine();

        Dictionary<ElementId, Snap> beforeAO, onAV, afterAO;
        long msAO1, msAV, msAO2;

        using (var t = new Transaction(doc, "TurboSpike round-trip toggle"))
        {
            t.Start();
            beforeAO = MeasureAt(doc, spaceIds, computeVolumes: false, out msAO1);  // AO baseline
            onAV     = MeasureAt(doc, spaceIds, computeVolumes: true,  out msAV);    // calc-phase mode
            afterAO  = MeasureAt(doc, spaceIds, computeVolumes: false, out msAO2);   // back to AO
            t.RollBack();                                                            // restore original setting
        }

        sb.AppendLine($"Regen cost   AO#1 {msAO1} ms   ->   A+V {msAV} ms   ->   AO#2 {msAO2} ms");
        sb.AppendLine();

        // (1) Round-trip non-destructiveness: BEFORE(AO) vs AFTER(AO).
        var changed = new List<string>();
        foreach (ElementId id in spaceIds)
        {
            Snap b = beforeAO[id], a = afterAO[id];
            if (b.Name != a.Name ||
                Math.Abs(b.Area - a.Area) > 0.01 ||
                b.LoopCount != a.LoopCount ||
                b.SegCount != a.SegCount ||
                Math.Abs(b.Perimeter - a.Perimeter) > 0.01)
            {
                changed.Add($"   {b.Name}: name {b.Name}->{a.Name}, area {b.Area:0.0}->{a.Area:0.0}, " +
                            $"loops {b.LoopCount}->{a.LoopCount}, segs {b.SegCount}->{a.SegCount}, " +
                            $"perim {b.Perimeter:0.00}->{a.Perimeter:0.00}");
            }
        }
        sb.AppendLine("(1) ROUND TRIP  AO -> A+V -> AO  (name/area/footprint must be identical)");
        if (changed.Count == 0)
            sb.AppendLine($"    OK — all {spaceIds.Count} spaces byte-identical before vs after. Toggle is non-destructive.");
        else
        {
            sb.AppendLine($"    {changed.Count} space(s) CHANGED across the round trip:");
            foreach (string line in changed.Take(20)) sb.AppendLine(line);
        }
        sb.AppendLine();

        // (2) Boundary availability under Areas+Volumes, incl. volume-collapsed spaces.
        int validBoundaryAV = onAV.Values.Count(s => s.LoopCount > 0 && s.SegCount >= 3);
        int collapsedAV = onAV.Values.Count(s => s.Volume <= 1.0);
        int collapsedButBounded = spaceIds.Count(id => onAV[id].Volume <= 1.0 && onAV[id].LoopCount > 0 && onAV[id].SegCount >= 3);
        sb.AppendLine("(2) GetBoundarySegments UNDER AREAS+VOLUMES (the calc-phase mode)");
        sb.AppendLine($"    spaces with valid boundary (loops>0, segs>=3) : {validBoundaryAV}/{spaceIds.Count}");
        sb.AppendLine($"    volume-collapsed spaces (vol<=1)              : {collapsedAV}");
        sb.AppendLine($"    ...of those, boundary STILL valid            : {collapsedButBounded}/{collapsedAV}  (want full)");
        sb.AppendLine();

        // Per-space detail (AO footprint fingerprint + A+V volume to show which collapse).
        sb.AppendLine("PER-SPACE   name | level | projElev | Area | loops/segs | perim | VolAV");
        foreach (ElementId id in spaceIds
                     .OrderBy(id => afterAO[id].LevelProjElev)
                     .ThenBy(id => afterAO[id].Name))
        {
            Snap s = afterAO[id];
            string collapse = onAV[id].Volume <= 1.0 ? "  (vol collapsed)" : "";
            sb.AppendLine($"   {Trunc(s.Name, 22),-22} | {Trunc(s.Level, 12),-12} | {s.LevelProjElev,8:0.00} | " +
                          $"{s.Area,7:0.0} | {s.LoopCount}/{s.SegCount,-3} | {s.Perimeter,7:0.0} | {onAV[id].Volume,8:0.0}{collapse}");
        }

        ShowReport(commandData, sb.ToString());
        return Result.Succeeded;
    }

    private static Dictionary<ElementId, Snap> MeasureAt(Document doc, List<ElementId> spaceIds, bool computeVolumes, out long regenMs)
    {
        AreaVolumeSettings.GetAreaVolumeSettings(doc).ComputeVolumes = computeVolumes;
        var sw = Stopwatch.StartNew();
        doc.Regenerate();
        sw.Stop();
        regenMs = sw.ElapsedMilliseconds;

        var opts = new SpatialElementBoundaryOptions();
        var result = new Dictionary<ElementId, Snap>();
        foreach (ElementId id in spaceIds)
        {
            var s = new Snap();
            var sp = doc.GetElement(id) as Space;
            if (sp != null)
            {
                s.Name = sp.Name ?? "?";
                s.Level = sp.Level?.Name ?? "-";
                s.LevelProjElev = sp.Level?.ProjectElevation ?? 0.0;
                s.Area = sp.Area;
                try { s.Volume = sp.Volume; } catch { s.Volume = 0.0; }

                IList<IList<BoundarySegment>> loops = null;
                try { loops = sp.GetBoundarySegments(opts); } catch { }
                if (loops != null)
                {
                    s.LoopCount = loops.Count;
                    foreach (IList<BoundarySegment> loop in loops)
                    {
                        s.SegCount += loop.Count;
                        foreach (BoundarySegment seg in loop)
                        {
                            Curve c = null;
                            try { c = seg.GetCurve(); } catch { }
                            if (c != null) s.Perimeter += c.Length;
                        }
                    }
                }
            }
            result[id] = s;
        }
        return result;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

    private static void ShowReport(ExternalCommandData commandData, string text)
    {
        var log = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            Text = text
        };

        var copy = new Button
        {
            Content = "Copy all",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        copy.Click += (s, e) => { try { Clipboard.SetText(text); } catch { } };

        var panel = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(copy, Dock.Top);
        panel.Children.Add(copy);
        panel.Children.Add(log);

        var window = new Window
        {
            Title = "TurboSpike",
            Width = 820,
            Height = 480,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        new WindowInteropHelper(window) { Owner = commandData.Application.MainWindowHandle };
        window.ShowDialog();
    }
}
