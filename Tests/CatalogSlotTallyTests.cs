using System.Collections.Generic;
using System.Linq;
using TurboSuite.Zones.Models;
using TurboSuite.Zones.Services;
using Xunit;

namespace TurboSuite.Tests.Zones
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for CatalogSlotTally (Core/Zones/Services/CatalogSlotTally.cs) — a control
    //  device's six catalog slots turned into order rows.
    //
    //  A keypad is not one part: base unit + button kits + faceplate is three lines and ONE device.
    //  The quantity grammar comes from the Counts module (CatalogQtyParser / CatalogQtyRule.Evaluate)
    //  and nothing else does — Lutron control devices are not declared in Counts, so its fixture
    //  model, cut-lists and blocking validator stay out.
    //
    //  For me (Claude): tokens are blank ⇒ 1 per device, "N" ⇒ N per device, "1/N" ⇒ 1 per N devices
    //  (ceil), "N @type" ⇒ N per type regardless of count, "N @ft"/"N @in" ⇒ stock-cut, which is
    //  invalid here because a control device has no Linear Length. Bad tokens fall back to 1-per and
    //  carry a Diagnostic; they are never dropped. Derivations inline.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    public class CatalogSlotTallyTests
    {
        private static IReadOnlyList<ControlDeviceTally> ForType(
            int instances, params (string Catalog, string Qty)[] slots)
            => CatalogSlotTally.ForType(
                "Test Type", instances,
                slots.Select(s => s.Catalog).ToList(),
                slots.Select(s => s.Qty).ToList());

        /// <summary>The case that motivated all of this: one two-gang keypad is a base unit, two button
        /// kits and a faceplate — three order lines, and (elsewhere) still two devices on the link.</summary>
        [Fact]
        public void OneKeypadTypeProducesItsWholeBillOfParts()
        {
            var rows = ForType(3,
                ("HQRD-2G-BASE", ""),
                ("HQRD-BTN-KIT", "2"),
                ("HQRD-2G-FACE", ""));

            Assert.Equal(3, rows.Count);
            Assert.Equal(("HQRD-2G-BASE", 3), (rows[0].CatalogNumber, rows[0].Quantity));
            Assert.Equal(("HQRD-BTN-KIT", 6), (rows[1].CatalogNumber, rows[1].Quantity));  // 2 per keypad
            Assert.Equal(("HQRD-2G-FACE", 3), (rows[2].CatalogNumber, rows[2].Quantity));
            Assert.All(rows, r => Assert.False(r.HasDiagnostic));
        }

        [Theory]
        [InlineData("", 7)]          // blank ⇒ one per device
        [InlineData("2", 14)]        // N per device
        [InlineData("1/2", 4)]       // one per 2 devices, rounded up
        [InlineData("1/3", 3)]       // ceil(7/3)
        [InlineData("2 @type", 2)]   // per type, count-independent
        public void QuantityTokensResolveAgainstTheDeviceCount(string token, int expected)
        {
            var row = Assert.Single(ForType(7, ("PART", token)));
            Assert.Equal(expected, row.Quantity);
            Assert.False(row.HasDiagnostic);
        }

        /// <summary>Stock-cut quantities divide a fixture's Linear Length by a stock length. A keypad
        /// has no length, so the token is a mis-authored slot rather than an unused mode — evaluating
        /// it with a zero length would silently return the device count and hide the mistake.</summary>
        [Theory]
        [InlineData("4 @ft")]
        [InlineData("48 @in")]
        public void StockLengthTokensAreRejectedOnAControlDevice(string token)
        {
            var row = Assert.Single(ForType(5, ("PART", token)));

            Assert.Equal(5, row.Quantity);            // falls back to one per device
            Assert.True(row.HasDiagnostic);
            Assert.Contains("Linear Length", row.Diagnostic);
        }

        /// <summary>A token nobody can parse still yields a line. A part nobody can parse is still a
        /// part somebody has to buy, and silence is the failure this area exists to remove — so the
        /// quantity falls back to something defensible and the row says why it is suspect.</summary>
        [Fact]
        public void UnparseableTokenFallsBackToOnePerDeviceAndComplains()
        {
            var row = Assert.Single(ForType(4, ("PART", "banana")));

            Assert.Equal(4, row.Quantity);
            Assert.True(row.HasDiagnostic);
            Assert.Contains("Test Type", row.Diagnostic);
            Assert.Contains("Catalog Qty1", row.Diagnostic);
            Assert.Contains("banana", row.Diagnostic);
        }

        /// <summary>Slot position is reported as authored, so the complaint points at the parameter the
        /// user has to open.</summary>
        [Fact]
        public void DiagnosticNamesTheSlotItCameFrom()
        {
            var rows = ForType(1, ("A", ""), ("B", ""), ("C", "1/0"));
            Assert.Contains("Catalog Qty3", rows[2].Diagnostic);
        }

        /// <summary>Empty slots are skipped, not counted as blank parts.</summary>
        [Fact]
        public void BlankSlotsAreSkipped()
        {
            var rows = ForType(2, ("BASE", ""), ("", "5"), ("FACE", ""));

            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { "BASE", "FACE" }, rows.Select(r => r.CatalogNumber));
        }

        /// <summary>A type declaring nothing at all still counts its devices — they are placed, so the
        /// quantity is real; it just has no part number to be ordered against.</summary>
        [Fact]
        public void TypeWithNoSlotsStillCountsItsDevices()
        {
            var row = Assert.Single(ForType(6));

            Assert.Equal(6, row.Quantity);
            Assert.False(row.HasCatalogNumber);
            Assert.Equal("Test Type", row.TypeName);
        }

        [Fact]
        public void NoInstancesProducesNoRows()
            => Assert.Empty(ForType(0, ("PART", "")));

        /// <summary>Merging is by catalog number across every type, so two families carrying the same
        /// model collapse to one order line.</summary>
        [Fact]
        public void MergeCollapsesSharedCatalogNumbers()
        {
            var merged = CatalogSlotTally.Merge(new List<ControlDeviceTally>
            {
                new ControlDeviceTally { CatalogNumber = "FACE", TypeName = "A", Quantity = 3 },
                new ControlDeviceTally { CatalogNumber = "BASE", TypeName = "A", Quantity = 3 },
                new ControlDeviceTally { CatalogNumber = "FACE", TypeName = "B", Quantity = 2 }
            });

            Assert.Equal(new[] { "BASE", "FACE" }, merged.Select(m => m.CatalogNumber));
            Assert.Equal(5, merged.Single(m => m.CatalogNumber == "FACE").Quantity);
        }

        /// <summary>Rows with no catalog number stay unmerged and sort last: they cannot be told apart
        /// by part number, and one anonymous line would hide which type needs fixing.</summary>
        [Fact]
        public void MergeKeepsUncataloguedRowsApartAndLast()
        {
            var merged = CatalogSlotTally.Merge(new List<ControlDeviceTally>
            {
                new ControlDeviceTally { TypeName = "Seetouch 5-Button", Quantity = 2 },
                new ControlDeviceTally { CatalogNumber = "BASE", TypeName = "A", Quantity = 1 },
                new ControlDeviceTally { TypeName = "Palladiom 2-Button", Quantity = 4 }
            });

            Assert.Equal("BASE", merged[0].CatalogNumber);
            Assert.Equal("Palladiom 2-Button", merged[1].TypeName);
            Assert.Equal("Seetouch 5-Button", merged[2].TypeName);
        }

        /// <summary>A complaint survives merging — otherwise a mis-authored type could be silenced by a
        /// well-authored one that happens to share a part number.</summary>
        [Fact]
        public void MergeCarriesTheDiagnosticThrough()
        {
            var merged = CatalogSlotTally.Merge(new List<ControlDeviceTally>
            {
                new ControlDeviceTally { CatalogNumber = "KIT", TypeName = "A", Quantity = 1 },
                new ControlDeviceTally { CatalogNumber = "KIT", TypeName = "B", Quantity = 1,
                    Diagnostic = "B — Catalog Qty2 \"oops\": Unrecognized format" }
            });

            var row = Assert.Single(merged);
            Assert.Equal(2, row.Quantity);
            Assert.True(row.HasDiagnostic);
        }

        [Fact]
        public void MergeHandlesNull() => Assert.Empty(CatalogSlotTally.Merge(null));

        /// <summary>
        /// A family carries two description fields and six catalog slots, so they pair by position:
        /// Catalog Number1 takes the built-in Description, Catalog Number2 takes Description2. Slots
        /// 3–6 have no field left to draw on and stay blank — honest rather than lossy, since nothing
        /// in the library uses them yet.
        /// </summary>
        [Fact]
        public void DescriptionsPairWithTheFirstTwoSlotsOnly()
        {
            var rows = CatalogSlotTally.ForType(
                "Seetouch 5-Button", 2,
                new[] { "BASE", "KIT", "FACE" },
                new[] { "", "2", "" },
                new[] { "5-Button Keypad", "Button Kit" });

            Assert.Equal("5-Button Keypad", rows[0].Description);
            Assert.Equal("Button Kit", rows[1].Description);
            Assert.Equal("", rows[2].Description);       // no field left for slot 3
        }

        /// <summary>With no catalog number at all, what the type IS is the only thing left to say — so
        /// the fallback row keeps the first description rather than going blank as well.</summary>
        [Fact]
        public void TypeWithNoSlotsStillCarriesItsDescription()
        {
            var row = Assert.Single(CatalogSlotTally.ForType(
                "Seetouch 5-Button", 3, null, null, new[] { "5-Button Keypad" }));

            Assert.False(row.HasCatalogNumber);
            Assert.Equal("5-Button Keypad", row.Description);
            Assert.Equal(3, row.Quantity);
        }

        /// <summary>Merging keeps the first non-blank description. A type that says nothing must not
        /// silence one that does just by being collected first.</summary>
        [Fact]
        public void MergePrefersTheFirstNonBlankDescription()
        {
            var merged = CatalogSlotTally.Merge(new List<ControlDeviceTally>
            {
                new ControlDeviceTally { CatalogNumber = "KIT", TypeName = "A", Quantity = 1, Description = "" },
                new ControlDeviceTally { CatalogNumber = "KIT", TypeName = "B", Quantity = 2, Description = "Button Kit" }
            });

            var row = Assert.Single(merged);
            Assert.Equal(3, row.Quantity);
            Assert.Equal("Button Kit", row.Description);
        }
    }
}
