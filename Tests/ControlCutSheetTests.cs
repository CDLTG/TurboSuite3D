using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dali;
using TurboSuite.Docs.Services;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Docs
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Control Cut Sheets — the resolver (part# / Model → cutsheet URL) and the pure row builder
    //  (resolve + URL-collapse + order + stable key) behind the TurboDocs Cut Sheets "Control Package".
    //
    //  The load-bearing test is CompletenessOfClosedSet: it walks every part number a BrandConfig /
    //  ShadeSolver / DaliSolver can emit and asserts each is accounted for (mapped to a URL or listed
    //  as deliberately cutsheet-less). A new control part added to a brand config with neither fails
    //  here, so a part can never silently vanish from the export.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    public class ControlCutSheetResolverTests
    {
        private static IEnumerable<string> AllEmittablePartNumbers()
        {
            var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var brand in new[]
                     {
                         BrandConfig.CreateLutron(useDedicatedRelayModule: false),
                         BrandConfig.CreateLutron(useDedicatedRelayModule: true),
                         BrandConfig.Crestron,
                     })
            {
                foreach (var pn in brand.PanelPartNumbers.Values) parts.Add(pn);
                foreach (var pn in brand.ModulePartNumbers.Values) parts.Add(pn);
                if (brand.SpecialDevices != null)
                    foreach (var pn in brand.SpecialDevices.Values) parts.Add(pn);
                if (!string.IsNullOrEmpty(brand.PowerSupplyPartNumber)) parts.Add(brand.PowerSupplyPartNumber);
                if (brand.WireHarnessPartNumbers != null)
                    foreach (var pn in brand.WireHarnessPartNumbers.Values) parts.Add(pn);
            }
            parts.Add(ShadeSolver.PanelPartNumber);
            parts.Add(DaliSolver.ModulePartNumber);
            return parts;
        }

        [Fact]
        public void CompletenessOfClosedSet_EveryEmittablePartIsAccountedFor()
        {
            var unaccounted = AllEmittablePartNumbers()
                .Where(pn => !ControlCutSheetUrlResolver.IsAccountedFor(pn))
                .ToList();

            Assert.True(unaccounted.Count == 0,
                "Control parts with no URL and not in KnownNoCutsheet: " + string.Join(", ", unaccounted));
        }

        [Theory]
        [InlineData("Alisse")]
        [InlineData("Cameo")]
        [InlineData("Horizon")]
        [InlineData("Palladiom")]
        [InlineData("seeTouch")]
        [InlineData("seeTouch Tabletop")]
        [InlineData("Signature")]
        [InlineData("Hybrid Repeater")]
        public void ResolveModel_KnownModels_ReturnUrl(string model)
        {
            Assert.False(string.IsNullOrEmpty(ControlCutSheetUrlResolver.ResolveModel(model)));
        }

        [Fact]
        public void ResolveModel_IsCaseAndWhitespaceInsensitive()
        {
            Assert.Equal(
                ControlCutSheetUrlResolver.ResolveModel("Palladiom"),
                ControlCutSheetUrlResolver.ResolveModel("  palladiom  "));
        }

        [Fact]
        public void Resolve_UnknownPartAndModel_ReturnEmpty()
        {
            Assert.Equal(string.Empty, ControlCutSheetUrlResolver.ResolvePart("NOT-A-PART"));
            Assert.Equal(string.Empty, ControlCutSheetUrlResolver.ResolveModel("Grafik Eye"));
        }

        [Fact]
        public void ResolvePart_WireHarness_HasNoUrlButIsAccounted()
        {
            Assert.Equal(string.Empty, ControlCutSheetUrlResolver.ResolvePart("PDW-QS-4"));
            Assert.True(ControlCutSheetUrlResolver.IsAccountedFor("PDW-QS-4"));
        }
    }

    public class ControlCutSheetRowBuilderTests
    {
        private static BomLineItem Line(string category, string part, string desc = "") =>
            new() { Category = category, PartNumber = part, Description = desc, Quantity = 1 };

        [Fact]
        public void SharedCutsheet_CollapsesToOneRow_LabelListsEveryPart()
        {
            var bom = new List<BomLineItem>
            {
                new() { IsHeader = true, Category = "Panels", Description = "Panels" },
                Line("Panels", "PD2-16F-120"),
                Line("Panels", "PD4-36F-120"),
                Line("Panels", "PD8-59F-120"),
            };

            var rows = ControlCutSheetRowBuilder.Build(bom, new List<string>(), new List<string>());

            Assert.Single(rows); // all three PD panels share one cutsheet
            Assert.Contains("PD2-16F-120", rows[0].Label);
            Assert.Contains("PD4-36F-120", rows[0].Label);
            Assert.Contains("PD8-59F-120", rows[0].Label);
            Assert.False(string.IsNullOrEmpty(rows[0].Url));
            Assert.Equal(rows[0].Url.ToLowerInvariant(), rows[0].Key); // key = normalized url
        }

        [Fact]
        public void HeadersWarningsAndUnmappedClosedParts_AreDropped()
        {
            var bom = new List<BomLineItem>
            {
                new() { IsHeader = true, Category = "Processors", Description = "Processors" },
                Line("Processors", "HQP7-2"),
                new() { IsWarning = true, Category = "Accessories", PartNumber = "HQP7-2", Description = "shortfall" },
                Line("Accessories", "PDW-QS-4"),     // known no-cutsheet → dropped
                Line("Accessories", "MADE-UP-PART"), // unmapped closed → dropped
            };

            var rows = ControlCutSheetRowBuilder.Build(bom, new List<string>(), new List<string>());

            Assert.Single(rows);
            Assert.Equal("HQP7-2", rows[0].Label);
        }

        [Fact]
        public void Keypads_UnresolvedModel_YieldsSkippedRow_WithModelKey()
        {
            var rows = ControlCutSheetRowBuilder.Build(
                new List<BomLineItem>(),
                keypadModels: new List<string> { "Palladiom", "Grafik Eye" },
                repeaterModels: new List<string>());

            var palladiom = rows.Single(r => r.Label == "Palladiom");
            Assert.False(string.IsNullOrEmpty(palladiom.Url));

            var unmapped = rows.Single(r => r.Label == "Grafik Eye");
            Assert.Equal(string.Empty, unmapped.Url);          // skipped: no cutsheet
            Assert.Equal("model:grafik eye", unmapped.Key);    // stable, non-empty key
        }

        [Fact]
        public void Ordering_IsClosedByCategory_ThenKeypadsAlpha_ThenRepeaters()
        {
            var bom = new List<BomLineItem>
            {
                Line("Modules", "LQSE-4A5-120-D"),
                Line("Processors", "HQP7-2"),
            };

            var rows = ControlCutSheetRowBuilder.Build(
                bom,
                keypadModels: new List<string> { "Signature", "Alisse" },
                repeaterModels: new List<string> { "Hybrid Repeater" });

            var labels = rows.Select(r => r.Label).ToList();
            Assert.Equal(
                new[] { "HQP7-2", "LQSE-4A5-120-D", "Alisse", "Signature", "Hybrid Repeater" },
                labels);
        }

        [Fact]
        public void Keypads_BlankAndDuplicateModels_AreDeduped()
        {
            var rows = ControlCutSheetRowBuilder.Build(
                new List<BomLineItem>(),
                keypadModels: new List<string> { "Palladiom", "palladiom", "  ", "" },
                repeaterModels: new List<string>());

            Assert.Single(rows);
            Assert.Equal("Palladiom", rows[0].Label);
        }
    }
}
