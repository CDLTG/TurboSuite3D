using System.Linq;
using TurboSuite.Shared.Hosting;
using TurboSuite.Snoop.Models;
using Xunit;

namespace TurboSuite.Tests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for HostReportTree (Core/Snoop/HostReportTree.cs) — renders a HostResolution into
    //  the SnoopNode tree the TurboSnoop window binds. Pins: the picked element stays OUT of the tree
    //  (it is the window header), every row is Info (no VG leaf bullet), and null host fields are
    //  omitted rather than printed as "Host category: ".
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    public class HostReportTreeTests
    {
        private static HostResolution ChurnRiskCasework() => new HostResolution(
            HostKind.LinkedElement, HostRiskTier.ChurnRisk,
            pickedLabel: "AL_Keypad : 2-Button (id 123)", pickedCategory: "Lighting Devices",
            hostLabel: "Closet Base : Type A (id 999)", hostCategory: "Casework",
            linkName: "Residence_ref.rvt", note: "churn note");

        [Fact]
        public void PickedElement_IsNotInTheTree()
        {
            SnoopNode root = HostReportTree.Build(ChurnRiskCasework());
            Assert.All(Flatten(root), n => Assert.DoesNotContain("AL_Keypad", n.Label));
        }

        [Fact]
        public void EveryRow_IsInfoKind_SoNoLeafBullet()
        {
            SnoopNode root = HostReportTree.Build(ChurnRiskCasework());
            // Root is the Family headline; every child is an Info row (never a VG Category/Subcategory).
            Assert.Equal(SnoopNodeKind.Family, root.Kind);
            Assert.All(root.Children, c => Assert.Equal(SnoopNodeKind.Info, c.Kind));
        }

        [Fact]
        public void ChurnRisk_HeadlineNamesCategoryAndRisk()
        {
            SnoopNode root = HostReportTree.Build(ChurnRiskCasework());
            Assert.Contains("Casework", root.Label);
            Assert.Contains("churn", root.Label.ToLowerInvariant());
        }

        [Fact]
        public void ChurnRisk_IncludesHostLinkAndNoteRows()
        {
            SnoopNode root = HostReportTree.Build(ChurnRiskCasework());
            Assert.Contains(root.Children, c => c.Label.Contains("Closet Base"));
            Assert.Contains(root.Children, c => c.Label.Contains("Casework"));
            Assert.Contains(root.Children, c => c.Label.Contains("Residence_ref.rvt"));
            Assert.Contains(root.Children, c => c.Label.Contains("churn note"));
        }

        [Fact]
        public void Unhosted_OmitsHostAndLinkRows()
        {
            var res = new HostResolution(
                HostKind.Unhosted, HostRiskTier.Unhosted,
                pickedLabel: "AL_Downlight : 3in (id 5)", pickedCategory: "Lighting Fixtures",
                hostLabel: null, hostCategory: null, linkName: null, note: "not hosted note");

            SnoopNode root = HostReportTree.Build(res);

            // Only the note row survives — no "Host element:", "Host category:", or "In link:" lines.
            Assert.DoesNotContain(root.Children, c => c.Label.StartsWith("Host element:"));
            Assert.DoesNotContain(root.Children, c => c.Label.StartsWith("Host category:"));
            Assert.DoesNotContain(root.Children, c => c.Label.StartsWith("In link:"));
            Assert.Contains(root.Children, c => c.Label.Contains("not hosted note"));
        }

        private static System.Collections.Generic.IEnumerable<SnoopNode> Flatten(SnoopNode node)
        {
            yield return node;
            foreach (SnoopNode child in node.Children)
                foreach (SnoopNode d in Flatten(child))
                    yield return d;
        }
    }
}
