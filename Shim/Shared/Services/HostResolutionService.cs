#nullable disable
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using TurboSuite.Shared.Hosting;

namespace TurboSuite.Shared.Services;

/// <summary>
/// The Revit-coupled half of host resolution: takes one of the user's own elements and works out what
/// it is hosted to — nothing (2D/free), an in-model element (a track fixture on its track), or an
/// element in a link (resolved, or unresolvable/orphaned). Hands the raw facts to the pure
/// <see cref="HostRiskClassifier"/> and returns a Revit-free <see cref="HostResolution"/>.
///
/// Why this shape: the resolution walk needs Revit types (Host / HostFace / RevitLinkInstance), so it
/// can't live in Core — but the RESULT and the risk classification are pure and do, so both the
/// single-element TurboSnoop report and the future full-model audit share one resolver over one model.
///
/// KEY MECHANISM (validated by a TurboSpike sweep, Revit 2025): for a link-hosted element,
/// <c>HostFace.LinkedElementId</c> resolves the specific host element — INCLUDING fixtures face-hosted
/// to a nested family (casework, doors) — even though resolving that same reference to a geometric
/// PlanarFace returns null (the case the wall-normal work retired). Element identity ≠ face geometry.
/// </summary>
public static class HostResolutionService
{
    /// <summary>Resolves the host story for a single element the user owns (the TurboSnoop pick path).</summary>
    public static HostResolution ResolveOne(FamilyInstance fi, Document doc)
    {
        string pickedLabel = Describe(fi);
        string pickedCategory = fi.Category?.Name;
        Element host = fi.Host;

        // No host → 2D / free placement.
        if (host == null)
            return Build(HostKind.Unhosted, pickedLabel, pickedCategory, null, null, null);

        // Hosted into a link: resolve the specific element via the host-face reference.
        if (host is RevitLinkInstance link)
        {
            string linkName = SafeName(link);
            Document linkDoc = link.GetLinkDocument();
            Reference face = fi.HostFace;
            ElementId linkedId = face?.LinkedElementId;

            Element hostElem = (linkDoc != null && linkedId != null && linkedId != ElementId.InvalidElementId)
                ? linkDoc.GetElement(linkedId)
                : null;

            if (hostElem != null)
                return Build(HostKind.LinkedElement, pickedLabel, pickedCategory,
                    Describe(hostElem), hostElem.Category?.Name, linkName);

            // Link known, host element not — stale id / no face id → likely orphaned.
            return Build(HostKind.LinkedUnresolved, pickedLabel, pickedCategory, null, null, linkName);
        }

        // Hosted to another element in THIS model (e.g. a track fixture on its track family).
        return Build(HostKind.HostDocElement, pickedLabel, pickedCategory,
            Describe(host), host.Category?.Name, null);
    }

    /// <summary>
    /// STUB — the future full-model host audit (report home TBD). It will reuse <see cref="ResolveOne"/>
    /// across a <c>FilteredElementCollector</c> of the user's own families and group the results by
    /// <see cref="HostRiskTier"/> (the churn-risk / orphaned buckets are the payoff). Left unimplemented
    /// deliberately: this pass ships the single-element path only, and no surface consumes a sweep yet.
    /// </summary>
    public static IReadOnlyList<HostResolution> ResolveAll(Document doc)
        => throw new NotImplementedException(
            "Full-model host audit is not built yet — report home TBD. See the header on this method.");

    private static HostResolution Build(HostKind kind, string pickedLabel, string pickedCategory,
        string hostLabel, string hostCategory, string linkName)
    {
        (HostRiskTier tier, string note) = HostRiskClassifier.Classify(kind, hostCategory);
        return new HostResolution(kind, tier, pickedLabel, pickedCategory, hostLabel, hostCategory, linkName, note);
    }

    private static string Describe(Element e)
    {
        if (e == null)
            return "(unresolved)";
        string fam = (e as FamilyInstance)?.Symbol?.FamilyName;
        return fam != null ? $"{fam} : {e.Name} (id {e.Id})" : $"{e.Name} (id {e.Id})";
    }

    private static string SafeName(Element e)
    {
        try { return e.Name; }
        catch { return "(unnamed link)"; }
    }
}
