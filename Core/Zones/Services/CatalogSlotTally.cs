#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Docs.Services;
using TurboSuite.Zones.Models;

namespace TurboSuite.Zones.Services
{
    /// <summary>
    /// Turns a control-device type's six catalog slots into order rows.
    ///
    /// A keypad is not one part: base unit + button kits + faceplate is three lines and one device.
    /// The slots already express that, and the quantity grammar that resolves them
    /// (<see cref="CatalogQtyParser"/>) is written and tested — so this borrows the parser and the
    /// arithmetic from the Counts module and <b>nothing else</b>. Lutron control devices are not
    /// declared in Counts at all; its fixture model, its length cut-lists and its blocking validator
    /// stay where they are.
    ///
    /// Lives in Core rather than in the Revit collector so the grammar's behaviour on control devices
    /// is pinned by tests instead of discovered in a model.
    /// </summary>
    public static class CatalogSlotTally
    {
        /// <summary>Catalog Number1–6 / Catalog Qty1–6.</summary>
        public const int SlotCount = 6;

        /// <summary>How many slots a family can describe. A type carries one built-in Description and
        /// one <c>Description2</c>, so the first two slots get words and the rest do not — see
        /// <see cref="ControlDeviceTally.Description"/>.</summary>
        public const int DescribedSlotCount = 2;

        /// <summary>
        /// One type's rows: every declared slot, quantity resolved against how many instances of that
        /// type are placed.
        ///
        /// A slot whose rule will not parse falls back to one-per-device and carries a
        /// <see cref="ControlDeviceTally.Diagnostic"/>. Falling back rather than dropping the slot is
        /// deliberate — a part nobody can parse is still a part somebody has to buy, and silence is
        /// the failure this whole area has been removing. The number is defensible and the design
        /// surface says why it is suspect.
        /// </summary>
        public static IReadOnlyList<ControlDeviceTally> ForType(
            string typeName,
            int instanceCount,
            IReadOnlyList<string?>? catalogNumbers,
            IReadOnlyList<string?>? qtyTokens,
            IReadOnlyList<string?>? descriptions = null)
        {
            var rows = new List<ControlDeviceTally>();
            if (instanceCount <= 0) return rows;

            for (int slot = 0; slot < SlotCount; slot++)
            {
                string catalog = At(catalogNumbers, slot).Trim();
                if (catalog.Length == 0) continue;

                string raw = At(qtyTokens, slot).Trim();
                var (rule, diagnostic) = ResolveRule(typeName, slot, raw);

                rows.Add(new ControlDeviceTally
                {
                    CatalogNumber = catalog,
                    TypeName = typeName,
                    // Linear length is 0 and stays 0: a control device has no length, which is exactly
                    // why the stock-cut mode is rejected above rather than evaluated with a zero.
                    Quantity = rule.Evaluate(instanceCount, 0),
                    Description = At(descriptions, slot).Trim(),
                    Diagnostic = diagnostic
                });
            }

            // No slot declared anything. The devices are placed, so the count is still real — it just
            // has no part number to be ordered against, which the BOM flags rather than drops. It keeps
            // the first description: with no catalog number, what the type IS is all there is to say.
            if (rows.Count == 0)
                rows.Add(new ControlDeviceTally
                {
                    TypeName = typeName,
                    Quantity = instanceCount,
                    Description = At(descriptions, 0).Trim()
                });

            return rows;
        }

        /// <summary>
        /// Collapses rows from every type onto one line per catalog number.
        ///
        /// Rows with no catalog number stay <b>unmerged</b> and sort last: they cannot be told apart by
        /// part number, and collapsing them into one anonymous line would hide which type needs fixing.
        ///
        /// Note the one mode where merging changes the answer: <c>N @type</c> is "N regardless of how
        /// many are placed", so two types declaring the same catalog number at <c>2 @type</c> merge to
        /// 4. That is the mode behaving as written — per type, not per job.
        /// </summary>
        public static List<ControlDeviceTally> Merge(IEnumerable<ControlDeviceTally>? rows)
        {
            var byCatalog = new Dictionary<string, ControlDeviceTally>(StringComparer.OrdinalIgnoreCase);
            var uncatalogued = new List<ControlDeviceTally>();

            foreach (var row in rows ?? Enumerable.Empty<ControlDeviceTally>())
            {
                if (row == null) continue;

                if (!row.HasCatalogNumber)
                {
                    uncatalogued.Add(row);
                    continue;
                }

                if (byCatalog.TryGetValue(row.CatalogNumber, out var existing))
                {
                    existing.Quantity += row.Quantity;
                    // First complaint wins. A second one on the same part number would name a
                    // different type, and one actionable pointer beats a concatenated list.
                    if (!existing.HasDiagnostic && row.HasDiagnostic)
                        existing.Diagnostic = row.Diagnostic;
                    // Likewise the first non-blank description: types sharing a part number describe
                    // the same object, so any of them will do, and a blank one should not win by
                    // arriving first.
                    if (existing.Description.Length == 0)
                        existing.Description = row.Description;
                }
                else
                {
                    byCatalog[row.CatalogNumber] = new ControlDeviceTally
                    {
                        CatalogNumber = row.CatalogNumber,
                        TypeName = row.TypeName,
                        Quantity = row.Quantity,
                        Description = row.Description,
                        Diagnostic = row.Diagnostic
                    };
                }
            }

            var merged = byCatalog.Values
                .OrderBy(t => t.CatalogNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();
            merged.AddRange(uncatalogued.OrderBy(t => t.TypeName, StringComparer.OrdinalIgnoreCase));
            return merged;
        }

        private static (CatalogQtyRule Rule, string? Diagnostic) ResolveRule(
            string typeName, int slot, string raw)
        {
            CatalogQtyRule rule;
            try
            {
                rule = CatalogQtyParser.Parse(raw);
            }
            catch (CatalogQtyParseException ex)
            {
                return (CatalogQtyRule.DefaultRule, Complaint(typeName, slot, raw, ex.Message));
            }

            // The stock-cut modes divide a fixture's Linear Length by a stock length. A keypad has no
            // length, so the mode is not merely unused here — it is a mis-authored slot, and letting
            // Evaluate fall through to the instance count would hide it.
            if (rule.Mode == CatalogQtyMode.Length)
            {
                return (CatalogQtyRule.DefaultRule, Complaint(typeName, slot, raw,
                    "stock-length quantities need a Linear Length, which a control device does not have"));
            }

            return (rule, null);
        }

        private static string Complaint(string typeName, int slot, string raw, string reason)
            => $"{typeName} — Catalog Qty{slot + 1} \"{raw}\": {reason}";

        private static string At(IReadOnlyList<string?>? values, int index)
            => values != null && index < values.Count ? values[index] ?? "" : "";
    }
}
