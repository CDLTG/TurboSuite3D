using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Docs.Models;
using TurboSuite.Zones.Models;

namespace TurboSuite.Docs.Services;

/// <summary>
/// Turns the collected control parts into the ordered, de-duplicated Cut Sheets row list.
///
/// Two inputs, two keys (see <see cref="ControlCutSheetUrlResolver"/>): the BOM line items (closed
/// hardware, keyed by part number) and the distinct placed keypad/repeater <c>Model</c> strings.
/// The interesting behaviour — resolve, collapse identical cutsheets to one page, order, and derive
/// the stable persistence key — is all here and pure, so the shim only maps the result onto
/// <c>FixtureSpecModel</c>.
///
/// Rules:
/// <list type="bullet">
///   <item><b>Closed parts</b> appear only if they resolve to a URL; an unmapped or known-no-cutsheet
///     part (the wire harnesses) is dropped silently — no row.</item>
///   <item><b>Keypads/repeaters</b> always appear (one row per distinct Model); an unresolved Model
///     yields a "no cutsheet" row (empty URL) that the grid shows greyed and the output skips.</item>
///   <item><b>Collapse by URL:</b> parts sharing a cutsheet (all PD-series panels) render once, the
///     row's label listing every part it covers.</item>
///   <item><b>Order:</b> closed parts in BOM emission order, then keypads (alphabetical by Model),
///     then repeaters — grid order == output order.</item>
/// </list>
/// </summary>
public static class ControlCutSheetRowBuilder
{
    // BOM categories in the order ControlBomBuilder emits them, so closed parts stay in a sensible
    // purchasing order even though we walk bomItems (which is already in this order — this is a
    // stable-sort key, a belt-and-suspenders against a caller handing them in unordered).
    private static readonly string[] CategoryOrder =
        { "Processors", "Panels", "Modules", "Accessories", "Shades" };

    public static List<ControlCutSheetRow> Build(
        IEnumerable<BomLineItem> bomItems,
        IReadOnlyList<string> keypadModels,
        IReadOnlyList<string> repeaterModels)
    {
        var candidates = new List<Candidate>();

        // --- Closed hardware, in BOM order (headers/warnings/blank part numbers dropped) ---
        var closed = (bomItems ?? Enumerable.Empty<BomLineItem>())
            .Where(i => i != null && !i.IsHeader && !i.IsWarning && !string.IsNullOrWhiteSpace(i.PartNumber))
            .OrderBy(i => Array.IndexOf(CategoryOrder, i.Category) is var idx && idx >= 0 ? idx : int.MaxValue);

        foreach (var item in closed)
        {
            string url = ControlCutSheetUrlResolver.ResolvePart(item.PartNumber);
            if (string.IsNullOrEmpty(url)) continue; // unmapped / known-no-cutsheet → no row
            candidates.Add(new Candidate(item.PartNumber.Trim(), item.Description ?? string.Empty, url, skipKey: null));
        }

        // --- Keypads then repeaters, alphabetical by Model. Unresolved Models still get a row. ---
        AddModels(candidates, keypadModels, "Keypad");
        AddModels(candidates, repeaterModels, "Repeater");

        return Collapse(candidates);
    }

    private static void AddModels(List<Candidate> candidates, IReadOnlyList<string> models, string kind)
    {
        if (models == null) return;
        var distinct = models
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase);

        foreach (var model in distinct)
        {
            string url = ControlCutSheetUrlResolver.ResolveModel(model);
            candidates.Add(new Candidate(model, kind, url, skipKey: "model:" + model.ToLowerInvariant()));
        }
    }

    // Collapse candidates that share a non-empty URL into one row (first occurrence sets position and
    // description; later ones append their label). Skipped rows (empty URL) each stand alone.
    private static List<ControlCutSheetRow> Collapse(List<Candidate> candidates)
    {
        var rows = new List<ControlCutSheetRow>();
        var labelsByRow = new List<List<string>>();
        var indexByUrl = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c.Url))
            {
                // No cutsheet — its own greyed row; keyed so blank/unmapped Models don't collide on "".
                rows.Add(new ControlCutSheetRow
                {
                    Label = c.Label,
                    Description = c.Description,
                    Url = string.Empty,
                    Key = c.SkipKey ?? c.Label,
                });
                labelsByRow.Add(new List<string> { c.Label });
                continue;
            }

            string norm = c.Url.Trim().ToLowerInvariant();
            if (indexByUrl.TryGetValue(norm, out int existing))
            {
                if (!labelsByRow[existing].Contains(c.Label, StringComparer.OrdinalIgnoreCase))
                {
                    labelsByRow[existing].Add(c.Label);
                    rows[existing].Label = string.Join(" / ", labelsByRow[existing]);
                }
                continue;
            }

            indexByUrl[norm] = rows.Count;
            rows.Add(new ControlCutSheetRow
            {
                Label = c.Label,
                Description = c.Description,
                Url = c.Url,
                Key = norm,
            });
            labelsByRow.Add(new List<string> { c.Label });
        }

        return rows;
    }

    private readonly struct Candidate
    {
        public Candidate(string label, string description, string url, string? skipKey)
        {
            Label = label;
            Description = description;
            Url = url;
            SkipKey = skipKey;
        }

        public string Label { get; }
        public string Description { get; }
        public string Url { get; }
        public string? SkipKey { get; }
    }
}
