#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TurboSuite.Shared.Helpers;

namespace TurboSuite.Spike;

/// <summary>
/// TurboSpike — the throwaway diagnostic bench. See the class rules in CLAUDE.md; clobber freely.
///
/// ACTIVE PROBE: parity + detection spike for the broad wall-face-normal fix
/// (WallFaceNormal_Broad_Fix_Plan.md). Select any mix of fixtures and run. Targeted selections to
/// exhaust the edge cases: MIRRORED wall fixtures (sconce/keypad/receptacle) that resolve a real
/// face; 2D UNHOSTED drafting sconces/keypads over CAD; a BACK-TO-BACK pair on one wall; angled /
/// sloped / curved walls; a rotated or mirrored arch link.
///
/// Per fixture it dumps three things:
///
/// (1) DIRECTION parity (Phases 1–3): old GeometryHelper.GetWallFaceNormal (reference path) vs the
///     new transform normal (Hand × Facing), AND a MIRROR-AWARE variant (Hand × Facing negated when
///     fixture.Mirrored). On every fixture that resolved a real PlanarFace, the correct transform
///     variant must agree (dot > 0.9). If RAW disagrees on a mirrored fixture but MIRROR-AWARE
///     agrees, that is the smoking gun that the helper needs the fixture.Mirrored correction.
///
/// (2) MIRROR audit: fixture.Mirrored, and specifically the count of mirrored AND face-resolved
///     fixtures (only those actually test the mirror hypothesis against the gate).
///
/// (3) DETECTION matrix (Phase 4 pre-work): old IsOnVerticalFace (reference-based, current strategy
///     selector) vs candidate transform predicates — A = host present AND Hand × Facing horizontally
///     usable; B = A OR Facing horizontal. Every A-vs-old / B-vs-old disagreement is a fixture Phase 4
///     would reclassify (wall <-> non-wall), i.e. change its whole Tag/Bubble placement strategy.
///     These are NOT bugs here — they are the reclassification list to eyeball before Phase 4.
///
/// Full dump -> temp file; summary + gates -> dialog.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class SpikeCommand : IExternalCommand
{
    private const double Eps = 0.001;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        var ids = uidoc.Selection.GetElementIds();
        if (ids.Count == 0)
        {
            TaskDialog.Show("TurboSpike — WallFaceNormal parity",
                "Select fixtures first. For edge cases, target: mirrored wall fixtures, 2D unhosted " +
                "sconces/keypads, a back-to-back wall pair, angled/curved walls.");
            return Result.Succeeded;
        }

        var sb = new StringBuilder();
        sb.AppendLine("TurboSpike — WallFaceNormal parity + detection");
        sb.AppendLine($"Doc: {doc.Title}   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('=', 100));

        int fixtures = 0;
        int planarResolvedCount = 0;
        int parityRawFailures = 0;          // dot(old,newRaw)   <= 0.9 on a PlanarFace-resolved fixture
        int parityMirrorAwareFailures = 0;  // dot(old,newMA)    <= 0.9 on a PlanarFace-resolved fixture
        int wallFixturesHandXFacing = 0;    // Hand × Facing horizontally usable
        int mirroredCount = 0;
        int mirroredAndResolved = 0;
        int mirrorSmokingGun = 0;           // mirrored + resolved + rawFlips but mirror-aware agrees
        int detectA_disagree = 0;           // candidate A != old IsOnVerticalFace
        int detectB_disagree = 0;           // candidate B != old IsOnVerticalFace

        var parityRawFailLines = new List<string>();
        var parityMAFailLines = new List<string>();
        var smokingGunLines = new List<string>();
        var detectALines = new List<string>();
        var detectBLines = new List<string>();

        foreach (ElementId id in ids)
        {
            if (doc.GetElement(id) is not FamilyInstance fi)
                continue;
            fixtures++;

            string fam = fi.Symbol?.Family?.Name ?? "?";
            string typ = fi.Symbol?.Name ?? "?";

            // --- Host / reference facts ---
            Element host = fi.Host;
            string hostKind = host == null ? "null" : host.GetType().Name;
            bool mirrored = fi.Mirrored;
            if (mirrored) mirroredCount++;
            Reference hostFaceRef = SafeHostFace(fi);
            string refType = hostFaceRef == null ? "no-HostFace"
                : hostFaceRef.ElementReferenceType.ToString();

            // --- Old path (reference walk) ---
            XYZ rawHostNormal = GeometryHelper.GetHostFaceNormal(fi); // null unless a PlanarFace resolved
            bool planarResolved = rawHostNormal != null;
            if (planarResolved) planarResolvedCount++;
            XYZ oldNormal = GeometryHelper.GetWallFaceNormal(fi);     // never null (falls back)

            // --- New path (transform), raw and mirror-aware ---
            XYZ handXfacing = fi.HandOrientation.CrossProduct(fi.FacingOrientation);
            XYZ handXfacingH = new XYZ(handXfacing.X, handXfacing.Y, 0);
            bool handXUsable = handXfacingH.GetLength() > Eps;
            if (handXUsable) wallFixturesHandXFacing++;

            XYZ newRaw = NormalFromTransform(fi, mirrorAware: false);
            XYZ newMA = NormalFromTransform(fi, mirrorAware: true);

            double dotRaw = oldNormal.DotProduct(newRaw);
            double dotMA = oldNormal.DotProduct(newMA);
            bool rawMaDiffer = newRaw.DotProduct(newMA) < 0.9; // only when mirrored + non-degenerate

            // --- Detection candidates (Phase 4) ---
            XYZ facingOr = fi.FacingOrientation;
            double facingZ = Math.Abs(facingOr.Z);
            XYZ facingH = new XYZ(facingOr.X, facingOr.Y, 0);
            bool facingHorizUsable = facingH.GetLength() > Eps;

            bool detOld = GeometryHelper.IsOnVerticalFace(fi);          // reference-based (current)
            bool detA = host != null && handXUsable;                    // strict: Hand × Facing usable
            bool detB = host != null && (handXUsable || facingHorizUsable); // broader: + horizontal facing

            if (planarResolved)
            {
                if (dotRaw <= 0.9)
                {
                    parityRawFailures++;
                    parityRawFailLines.Add($"  raw   id {id} {fam}:{typ}  mirrored={mirrored}  dot={dotRaw:F4}  old={V(oldNormal)} new={V(newRaw)}");
                }
                if (dotMA <= 0.9)
                {
                    parityMirrorAwareFailures++;
                    parityMAFailLines.Add($"  MA    id {id} {fam}:{typ}  mirrored={mirrored}  dot={dotMA:F4}  old={V(oldNormal)} newMA={V(newMA)}");
                }
                if (mirrored)
                {
                    mirroredAndResolved++;
                    if (dotRaw <= 0.9 && dotMA > 0.9)
                    {
                        mirrorSmokingGun++;
                        smokingGunLines.Add($"  id {id} {fam}:{typ}  RAW dot={dotRaw:F4} (flip)  MIRROR-AWARE dot={dotMA:F4} (agrees)");
                    }
                }
            }

            if (detA != detOld)
            {
                detectA_disagree++;
                detectALines.Add($"  A id {id} {fam}:{typ}  old={detOld} A={detA}  |Facing.Z|={facingZ:F2} handXusable={handXUsable}");
            }
            if (detB != detOld)
            {
                detectB_disagree++;
                detectBLines.Add($"  B id {id} {fam}:{typ}  old={detOld} B={detB}  |Facing.Z|={facingZ:F2} facingHoriz={facingHorizUsable}");
            }

            sb.AppendLine($"[{fixtures}] id {id.ToString()}  {fam} : {typ}");
            sb.AppendLine($"      host={hostKind}  mirrored={mirrored}  hostFaceRefType={refType}  planarFaceResolved={planarResolved}");
            sb.AppendLine($"      Facing={V(facingOr)}  Hand={V(fi.HandOrientation)}  |Facing.Z|={facingZ:F3}");
            sb.AppendLine($"      OLD (ref)        = {V(oldNormal)}   {(planarResolved ? "[from PlanarFace]" : "[FELL BACK — no PlanarFace]")}");
            sb.AppendLine($"      NEW (xform raw)  = {V(newRaw)}   handXusable={handXUsable}");
            sb.AppendLine($"      NEW (mirror-aware)= {V(newMA)}   {(rawMaDiffer ? "*** differs from raw (mirrored) ***" : "(== raw)")}");
            if (planarResolved)
            {
                string dv = dotRaw > 0.9 ? "AGREE"
                    : (dotMA > 0.9 ? "*** RAW FLIP — mirror-aware fixes it ***" : "*** DISAGREE (both) ***");
                sb.AppendLine($"      dot(old,raw)={dotRaw:F4}  dot(old,MA)={dotMA:F4}  {dv}");
            }
            else
            {
                sb.AppendLine($"      dot(old,raw)={dotRaw:F4}  dot(old,MA)={dotMA:F4}  (no PlanarFace to gate against)");
            }
            sb.AppendLine($"      DETECT: old(IsOnVerticalFace)={detOld}  A(hand×facing)={detA}{(detA != detOld ? " <<DIFF" : "")}  B(+horizFacing)={detB}{(detB != detOld ? " <<DIFF" : "")}");
            sb.AppendLine();
        }

        sb.AppendLine(new string('=', 100));
        sb.AppendLine($"fixtures={fixtures}  planarFaceResolved={planarResolvedCount}");
        sb.AppendLine($"DIRECTION parity failures: raw={parityRawFailures}  mirror-aware={parityMirrorAwareFailures}");
        sb.AppendLine($"MIRROR: mirrored={mirroredCount}  mirrored&resolved={mirroredAndResolved}  smokingGun(raw flips, MA fixes)={mirrorSmokingGun}");
        sb.AppendLine($"wallFixtures(hand×facing usable)={wallFixturesHandXFacing}");
        sb.AppendLine($"DETECTION disagreements vs old IsOnVerticalFace: A={detectA_disagree}  B={detectB_disagree}");

        string dirGate = parityRawFailures == 0
            ? "DIRECTION GATE PASS (raw) — transform normal agrees with every PlanarFace-resolved old normal."
            : (parityMirrorAwareFailures == 0
                ? $"DIRECTION GATE: raw FAILS on {parityRawFailures} fixture(s) but MIRROR-AWARE passes — helper needs the fixture.Mirrored correction."
                : $"DIRECTION GATE FAIL — {parityRawFailures} raw / {parityMirrorAwareFailures} mirror-aware disagreements. Investigate.");
        sb.AppendLine(dirGate);

        AppendList(sb, "RAW parity failures", parityRawFailLines);
        AppendList(sb, "MIRROR-AWARE parity failures", parityMAFailLines);
        AppendList(sb, "MIRROR smoking-gun (raw flips, mirror-aware fixes)", smokingGunLines);
        AppendList(sb, "DETECT A-vs-old disagreements (Phase 4 reclassify list)", detectALines);
        AppendList(sb, "DETECT B-vs-old disagreements (Phase 4 reclassify list)", detectBLines);

        // Full dump -> temp file; summary in the dialog.
        string path = Path.Combine(Path.GetTempPath(),
            $"TurboSpike_WallFaceNormal_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        try { File.WriteAllText(path, sb.ToString()); }
        catch { path = "(could not write temp file)"; }

        var summary = new StringBuilder();
        summary.AppendLine($"Fixtures: {fixtures}   PlanarFace-resolved: {planarResolvedCount}");
        summary.AppendLine();
        summary.AppendLine($"DIRECTION parity failures — raw: {parityRawFailures}   mirror-aware: {parityMirrorAwareFailures}");
        summary.AppendLine($"MIRROR — mirrored: {mirroredCount}   mirrored&resolved: {mirroredAndResolved}   smoking-gun: {mirrorSmokingGun}");
        summary.AppendLine($"DETECTION disagreements vs old — A: {detectA_disagree}   B: {detectB_disagree}");
        summary.AppendLine();
        summary.AppendLine(dirGate);
        if (mirroredAndResolved == 0 && mirroredCount > 0)
            summary.AppendLine("NOTE: mirrored fixtures present but none resolved a PlanarFace — mirror hypothesis still untested by the gate.");
        if (mirroredCount == 0)
            summary.AppendLine("NOTE: no mirrored fixtures in this selection — mirror hypothesis untested. Select mirrored wall fixtures.");
        summary.AppendLine();
        summary.AppendLine($"Full dump: {path}");

        var td = new TaskDialog("TurboSpike — WallFaceNormal parity + detection")
        {
            MainInstruction = dirGate,
            MainContent = summary.ToString()
        };
        td.Show();

        return Result.Succeeded;
    }

    /// <summary>
    /// Transform-derived outward wall normal, mirroring the validated helper's priority rule.
    /// When <paramref name="mirrorAware"/>, negates Hand × Facing for a mirrored instance (left-handed
    /// basis) before horizontalizing — the hypothesis under test.
    /// </summary>
    private static XYZ NormalFromTransform(FamilyInstance fi, bool mirrorAware)
    {
        XYZ cross = fi.HandOrientation.CrossProduct(fi.FacingOrientation);
        if (mirrorAware && fi.Mirrored) cross = cross.Negate();

        XYZ horizontal = new XYZ(cross.X, cross.Y, 0);
        if (horizontal.GetLength() > Eps)
            return horizontal.Normalize();

        XYZ facing = fi.FacingOrientation;
        XYZ facingH = new XYZ(facing.X, facing.Y, 0);
        return facingH.GetLength() > Eps ? facingH.Normalize() : XYZ.BasisY;
    }

    private static void AppendList(StringBuilder sb, string title, List<string> lines)
    {
        if (lines.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine($"--- {title} ({lines.Count}) ---");
        foreach (var l in lines) sb.AppendLine(l);
    }

    private static Reference SafeHostFace(FamilyInstance fi)
    {
        try { return fi.HostFace; }
        catch { return null; }
    }

    private static string V(XYZ v)
        => v == null ? "null" : $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";
}
