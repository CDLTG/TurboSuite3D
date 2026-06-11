#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;

namespace TurboSuite.Name.Services;

/// <summary>
/// Pure ACadSharp introspection over a loaded <see cref="CadDocument"/>, powering the in-app
/// CAD Room Source discovery (Settings dialog). Supplies the block names + attribute tags that
/// Revit's native Query tool reports as N/A, and classifies a clicked room label as Block vs Text
/// using the validated, layer-constrained rule (no distance heuristic).
/// </summary>
public static class CadIntrospectionService
{
    /// <summary>Result of resolving the entity under a "Pick from view" click.</summary>
    public sealed class CadPickResult
    {
        public bool IsBlock;
        public string BlockName;
        public List<string> Tags = new();
        /// <summary>
        /// The clicked insert's attribute tag→value pairs, in as-drawn order (not sorted/deduped).
        /// Lets the user tell which tag holds the room name vs. the ceiling height by reading the
        /// actual values, instead of guessing from cryptic tag names.
        /// </summary>
        public List<KeyValuePair<string, string>> TagValues = new();
        public string Layer;
    }

    /// <summary>Distinct entity layer names, alpha-sorted. Derived from entities (the proven pattern).</summary>
    public static List<string> GetLayers(CadDocument cadDoc)
    {
        return cadDoc.Entities
            .Select(e => e.Layer?.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Distinct block names actually referenced by an <see cref="Insert"/> (the real domain),
    /// excluding <c>*</c>-prefixed anonymous defs, alpha-sorted.
    /// </summary>
    public static List<string> GetReferencedBlockNames(CadDocument cadDoc)
    {
        return cadDoc.Entities
            .OfType<Insert>()
            .Select(i => i.Block?.Name)
            .Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith("*"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Distinct attribute tags across all inserts of the given block, alpha-sorted.</summary>
    public static List<string> GetAttributeTags(CadDocument cadDoc, string blockName)
    {
        if (string.IsNullOrEmpty(blockName)) return new List<string>();

        return cadDoc.Entities
            .OfType<Insert>()
            .Where(i => string.Equals(i.Block?.Name, blockName, StringComparison.OrdinalIgnoreCase)
                        && i.Attributes != null)
            .SelectMany(i => i.Attributes.Select(a => a.Tag))
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Validated classification rule. Constrained to <paramref name="layerName"/>: an attributed
    /// <see cref="Insert"/> on that layer → Block (block name + its attribute tags); else a
    /// <see cref="TextEntity"/>/<see cref="MText"/> on that layer → Text. Distance to
    /// <c>InsertPoint</c> is only a tiebreaker among same-layer room blocks (which share a block
    /// name anyway). Returns null if neither exists on the layer.
    /// </summary>
    public static CadPickResult ResolveAtPoint(CadDocument cadDoc, double dwgX, double dwgY, string layerName)
    {
        if (string.IsNullOrEmpty(layerName)) return null;

        bool OnLayer(Entity e) => string.Equals(e.Layer?.Name, layerName, StringComparison.OrdinalIgnoreCase);

        // Block: attributed insert on the clicked layer (nearest by InsertPoint as tiebreaker).
        Insert nearest = null;
        double bestDist = double.MaxValue;
        foreach (var insert in cadDoc.Entities.OfType<Insert>())
        {
            if (!OnLayer(insert)) continue;
            if (insert.Attributes == null || insert.Attributes.Count == 0) continue;

            double dx = insert.InsertPoint.X - dwgX;
            double dy = insert.InsertPoint.Y - dwgY;
            double dist = dx * dx + dy * dy;
            if (dist < bestDist)
            {
                bestDist = dist;
                nearest = insert;
            }
        }

        if (nearest != null)
        {
            return new CadPickResult
            {
                IsBlock = true,
                BlockName = nearest.Block?.Name ?? "",
                Layer = layerName,
                Tags = nearest.Attributes
                    .Select(a => a.Tag)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                TagValues = nearest.Attributes
                    .Where(a => !string.IsNullOrEmpty(a.Tag))
                    .Select(a => new KeyValuePair<string, string>(
                        a.Tag, CadRoomExtractorService.StripCadFormatting(a.Value ?? "").Trim()))
                    .ToList()
            };
        }

        // Text: any single-line/multiline text on the clicked layer.
        bool hasText = cadDoc.Entities.Any(e => OnLayer(e) && (e is TextEntity || e is MText));
        if (hasText)
        {
            return new CadPickResult
            {
                IsBlock = false,
                Layer = layerName
            };
        }

        return null;
    }
}
