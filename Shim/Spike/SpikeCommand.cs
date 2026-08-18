#nullable disable
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TurboSuite.Spike;

/// <summary>
/// TurboSpike — the throwaway diagnostic bench. See the class rules in CLAUDE.md.
///
/// CURRENT PROBE (host-resolution feasibility for a proposed "what am I hosted to?" reporter):
/// Sweeps the host doc's own lighting/electrical devices+fixtures and, for each hosted one
/// (HostFace != null), tries to resolve the LINKED element it is hosted to — RevitLinkInstance host →
/// GetLinkDocument → GetElement(HostFace.LinkedElementId) — and reports that element's category /
/// family / type. The question we need settled: does HostFace.LinkedElementId resolve to the real
/// casework/wall instance even for fixtures face-hosted to a NESTED family (the case where resolving
/// the reference to a geometric PlanarFace returned null in the wall-normal work)? Read the dialog:
/// the "resolved to non-Wall" bucket is the payoff — if casework-hosted keypads land there with a
/// real family:type, the reporter is straightforward; if they show up under "UNRESOLVED", we need the
/// geometric-proximity fallback instead.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public class SpikeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc == null)
        {
            TaskDialog.Show("TurboSpike", "No active document.");
            return Result.Succeeded;
        }

        var categories = new[]
        {
            BuiltInCategory.OST_LightingDevices,
            BuiltInCategory.OST_LightingFixtures,
            BuiltInCategory.OST_ElectricalFixtures,
        };

        var filter = new ElementMulticategoryFilter(categories);
        var instances = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .WherePasses(filter)
            .Cast<FamilyInstance>()
            .ToList();

        int total = instances.Count;
        int unhosted = 0;          // Host == null && HostFace == null
        int hostedNoFace = 0;      // Host != null but HostFace == null
        int hostNotLink = 0;       // Host present and is NOT a RevitLinkInstance (direct host-doc host)
        int linkNoDoc = 0;         // host link's document not loaded
        int resolved = 0;          // HostFace.LinkedElementId resolved to a real linked element
        int unresolvedLinkedId = 0;// HostFace present, host is link, but LinkedElementId did not resolve

        // resolved host category -> count
        var byHostCategory = new Dictionary<string, int>();
        // interesting sample: fixtures whose resolved host category != "Walls"
        var nonWallSamples = new List<string>();
        // sample of the unresolved-but-link-hosted cases (the failure mode we care about)
        var unresolvedSamples = new List<string>();

        foreach (FamilyInstance fi in instances)
        {
            Element host = fi.Host;
            Reference hostFace = fi.HostFace;

            if (host == null && hostFace == null)
            {
                unhosted++;
                continue;
            }

            if (hostFace == null)
            {
                hostedNoFace++;
                continue;
            }

            if (host is not RevitLinkInstance link)
            {
                hostNotLink++;
                // Direct host-doc host — record its category so we see how often this happens.
                string hc = host?.Category?.Name ?? "(host has no category)";
                Bump(byHostCategory, "[host-doc] " + hc);
                continue;
            }

            Document linkDoc = link.GetLinkDocument();
            if (linkDoc == null)
            {
                linkNoDoc++;
                continue;
            }

            ElementId linkedId = hostFace.LinkedElementId;
            Element hostElem = (linkedId != null && linkedId != ElementId.InvalidElementId)
                ? linkDoc.GetElement(linkedId)
                : null;

            if (hostElem == null)
            {
                unresolvedLinkedId++;
                if (unresolvedSamples.Count < 15)
                    unresolvedSamples.Add(
                        $"{Describe(fi)}  |  Host link: {SafeName(link)}  |  LinkedElementId: " +
                        $"{(linkedId == null ? "null" : linkedId.ToString())}");
                continue;
            }

            resolved++;
            string cat = hostElem.Category?.Name ?? "(no category)";
            Bump(byHostCategory, cat);

            if (!string.Equals(cat, "Walls", System.StringComparison.OrdinalIgnoreCase)
                && nonWallSamples.Count < 25)
            {
                nonWallSamples.Add($"{Describe(fi)}  →  hosted to [{cat}] {DescribeHost(hostElem)}");
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Swept {total} own fixtures/devices (Lighting Devices/Fixtures + Electrical Fixtures).");
        sb.AppendLine();
        sb.AppendLine($"Unhosted (Host & HostFace null):        {unhosted}");
        sb.AppendLine($"Hosted but HostFace null:               {hostedNoFace}");
        sb.AppendLine($"Host is direct (not a link):            {hostNotLink}");
        sb.AppendLine($"Host link doc not loaded:               {linkNoDoc}");
        sb.AppendLine($"RESOLVED linked host element:           {resolved}");
        sb.AppendLine($"UNRESOLVED (link-hosted, id failed):    {unresolvedLinkedId}");
        sb.AppendLine();
        sb.AppendLine("── Resolved host categories ──");
        foreach (var kv in byHostCategory.OrderByDescending(k => k.Value))
            sb.AppendLine($"  {kv.Value,4}  {kv.Key}");
        sb.AppendLine();

        if (nonWallSamples.Count > 0)
        {
            sb.AppendLine($"── Resolved to NON-Wall host (first {nonWallSamples.Count}) ──");
            foreach (string s in nonWallSamples) sb.AppendLine("  " + s);
            sb.AppendLine();
        }

        if (unresolvedSamples.Count > 0)
        {
            sb.AppendLine($"── UNRESOLVED link-hosted samples (first {unresolvedSamples.Count}) ──");
            foreach (string s in unresolvedSamples) sb.AppendLine("  " + s);
        }

        var td = new TaskDialog("TurboSpike — host resolution")
        {
            MainInstruction = $"{resolved} resolved / {unresolvedLinkedId} unresolved (of {total})",
            MainContent = sb.ToString(),
        };
        td.Show();
        return Result.Succeeded;
    }

    private static void Bump(Dictionary<string, int> map, string key)
        => map[key] = map.TryGetValue(key, out int n) ? n + 1 : 1;

    private static string Describe(FamilyInstance fi)
        => $"{fi.Symbol?.FamilyName} : {fi.Name} (id {fi.Id})";

    private static string DescribeHost(Element host)
    {
        string fam = (host as FamilyInstance)?.Symbol?.FamilyName;
        string type = host.Name;
        return fam != null ? $"{fam} : {type} (id {host.Id})" : $"{type} (id {host.Id})";
    }

    private static string SafeName(Element e)
    {
        try { return e.Name; } catch { return "(unnamed)"; }
    }
}
