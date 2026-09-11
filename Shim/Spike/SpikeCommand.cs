#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using static Autodesk.Revit.DB.BuiltInCategory;

namespace TurboSuite.Spike;

/// <summary>
/// TurboSpike — the throwaway diagnostic bench. See the class rules in CLAUDE.md.
///
/// Overwrite-safe by design: everything in <see cref="Execute"/> is diagnostics-only scratch. When
/// you need to answer a question the running model can settle (a parameter's StorageType/writability,
/// whether an API member exists on this version, a family's connectors/geometry), clobber whatever
/// stub is here with a probe, have the user build and run it, and read the dialog. No prior spike is
/// worth preserving. It ships gated behind ExperimentalCommandsEnabled, so it's dev-only.
///
/// CURRENT PROBE — "TurboSuite Role" authoring audit. Coverage net for the name-decoupling migration
/// (see the plan). Load every authored family into a project, run this, and read the report it writes
/// to %TEMP%\TurboSuite_RoleAudit.txt. It flags: unrecognized role strings (typos / wrong bucket),
/// families whose types disagree (value set on some types but not all — the value is a plain
/// read/write field, so every type must carry it), missing finder roles, and duplicate finder roles
/// within a category (which make the finder nondeterministic). Nothing here is shipped or depended on;
/// the recognized-role vocabulary is inlined because the real Roles class isn't built yet.
/// </summary>
[Transaction(TransactionMode.Manual)]
public class SpikeCommand : IExternalCommand
{
    const string RoleParam = "TurboSuite Role";

    // Frozen classify vocabulary (DECIDED, plan classify table).
    static readonly string[] ClassifyRoles =
    {
        "Sconce", "Chandelier", "PictureLight", "WallVertical", "CeilingFan",
        "ExhaustFan", "FireplaceIgniter", "Receptacle", "Switch", "Keypad",
        "DriverPlaceholder", "HybridRepeater",
    };

    // Frozen finder vocabulary (LOCKED 2026-09-10, plan finder table).
    static readonly string[] FinderRoles =
    {
        "FixtureTypeTag", "LinearTag", "RunLengthTag", "SwitchIdTag", "KeypadTag",
        "DeviceTypeTag", "FixtureSwitchlegTag", "RemoteSwitchlegTag",
        "ElectricalSwitchlegTag", "DeviceSwitchlegTag", "LinearFeedTag", "LinearFeedDetail",
        "DmxDecoderDetail", "DmxDriverDetail", "DmxInterfaceDetail", "DmxProcessorDetail",
        "DmxTerminatorDetail", "DmxWireMarkAnnotation",
    };

    // Where each kind of role is authored. If a special family lives outside these, it won't be
    // scanned — a finder family in an unscanned category surfaces as MISSING (a prompt to look).
    static readonly BuiltInCategory[] ModelCats =
        { OST_LightingFixtures, OST_LightingDevices, OST_ElectricalFixtures };

    static readonly BuiltInCategory[] FinderCats =
        { OST_LightingFixtureTags, OST_LightingDeviceTags, OST_ElectricalFixtureTags,
          OST_DetailComponents, OST_GenericAnnotation };

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document doc = commandData.Application.ActiveUIDocument.Document;

        var symbols = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("TurboSuite Role — authoring audit");
        sb.AppendLine("Generated: " + DateTime.Now);
        sb.AppendLine("Project:   " + (string.IsNullOrEmpty(doc.Title) ? "(untitled)" : doc.Title));
        sb.AppendLine();
        sb.AppendLine("Classify categories scanned: " + string.Join(", ", ModelCats));
        sb.AppendLine("Finder categories scanned:   " + string.Join(", ", FinderCats));
        sb.AppendLine("(A family loaded into a category NOT listed here is invisible to this audit.)");
        sb.AppendLine();

        int flags = 0;
        flags += AuditBucket(sb, symbols, ModelCats, ClassifyRoles,
            "CLASSIFY  (model families)", isFinder: false);
        flags += AuditBucket(sb, symbols, FinderCats, FinderRoles,
            "FIND  (tag / detail / annotation families)", isFinder: true);

        // Key the filename to the project so separate 2D / 3D audits don't overwrite each other.
        string safeTitle = string.Concat(
            (string.IsNullOrEmpty(doc.Title) ? "untitled" : doc.Title).Split(Path.GetInvalidFileNameChars()));
        string path = Path.Combine(Path.GetTempPath(), $"TurboSuite_RoleAudit_{safeTitle}.txt");
        File.WriteAllText(path, sb.ToString());

        var td = new TaskDialog("TurboSpike — Role audit")
        {
            MainInstruction = flags == 0
                ? "No problems flagged."
                : flags + " problem(s) flagged — see the report.",
            MainContent = "Full report written to:\n" + path
                + "\n\nScan CLASSIFY to confirm every special family you authored appears under the "
                + "right role, and FIND for any MISSING or DUPLICATE role. Families that came back "
                + "blank are expected (most families carry no role).",
        };
        td.Show();

        return Result.Succeeded;
    }

    /// <summary>
    /// Bucket the in-scope symbols by family, canonicalize each family's role value (trim +
    /// case-insensitive), and append a section: authored coverage grouped by role, plus flagged
    /// problems. Returns the flag count for the summary dialog.
    /// </summary>
    static int AuditBucket(StringBuilder sb, List<FamilySymbol> allSymbols,
        BuiltInCategory[] cats, string[] knownRoles, string title, bool isFinder)
    {
        var known = new HashSet<string>(knownRoles, StringComparer.OrdinalIgnoreCase);
        var catSet = new HashSet<BuiltInCategory>(cats);

        var families = allSymbols
            .Where(s => s.Category != null && catSet.Contains(s.Category.BuiltInCategory))
            .GroupBy(s => new { Cat = s.Category.Name, Fam = s.FamilyName })
            .ToList();

        var authored = new List<(string role, string fam, string cat)>();  // recognized + consistent
        var unrecognized = new List<(string fam, string cat, string val)>();
        var inconsistent = new List<(string fam, string cat, string vals)>();
        int blankCount = 0;

        foreach (var g in families)
        {
            var values = g
                .Select(s => (s.LookupParameter(RoleParam)?.AsString() ?? string.Empty).Trim())
                .Distinct()
                .ToList();

            if (values.All(v => v.Length == 0)) { blankCount++; continue; }

            if (values.Count > 1)
            {
                inconsistent.Add((g.Key.Fam, g.Key.Cat,
                    string.Join(" | ", values.Select(v => v.Length == 0 ? "(blank)" : v))));
                continue;
            }

            string val = values[0];
            string canon = known.FirstOrDefault(k => string.Equals(k, val, StringComparison.OrdinalIgnoreCase));
            if (canon == null) unrecognized.Add((g.Key.Fam, g.Key.Cat, val));
            else authored.Add((canon, g.Key.Fam, g.Key.Cat));
        }

        sb.AppendLine("================================================================");
        sb.AppendLine(title);
        sb.AppendLine("================================================================");
        sb.AppendLine($"Families scanned: {families.Count}   (blank: {blankCount}, authored: {authored.Count})");
        sb.AppendLine();

        sb.AppendLine("-- Authored roles --");
        foreach (var role in knownRoles)
        {
            var fams = authored.Where(a => a.role == role)
                .Select(a => a.fam + "   [" + a.cat + "]")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (fams.Count == 0)
                sb.AppendLine($"  {role}: (none)" + (isFinder ? "   <-- MISSING" : ""));
            else
            {
                sb.AppendLine($"  {role}:");
                foreach (var f in fams) sb.AppendLine("      " + f);
            }
        }
        sb.AppendLine();

        int flags = unrecognized.Count + inconsistent.Count;

        if (unrecognized.Count > 0)
        {
            sb.AppendLine("-- UNRECOGNIZED values (typo, or wrong classify/finder bucket) --");
            foreach (var u in unrecognized.OrderBy(u => u.fam, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"  \"{u.val}\"  on  {u.fam}   [{u.cat}]");
            sb.AppendLine();
        }

        if (inconsistent.Count > 0)
        {
            sb.AppendLine("-- INCONSISTENT across types (set the value on EVERY type of the family) --");
            foreach (var i in inconsistent.OrderBy(i => i.fam, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"  {i.fam}   [{i.cat}]:  {i.vals}");
            sb.AppendLine();
        }

        if (isFinder)
        {
            flags += knownRoles.Count(r => authored.All(a => a.role != r));  // missing roles

            var dupes = authored
                .GroupBy(a => new { a.role, a.cat })
                .Where(gr => gr.Count() > 1)
                .ToList();
            flags += dupes.Count;

            if (dupes.Count > 0)
            {
                sb.AppendLine("-- DUPLICATE finder role in one category (finder result is nondeterministic) --");
                foreach (var d in dupes)
                {
                    sb.AppendLine($"  {d.Key.role}  in  [{d.Key.cat}]:");
                    foreach (var a in d) sb.AppendLine("      " + a.fam);
                }
                sb.AppendLine();
            }
        }

        return flags;
    }
}
