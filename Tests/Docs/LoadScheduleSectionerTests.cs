using System.Collections.Generic;
using System.Linq;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using Xunit;

namespace TurboSuite.Tests.Docs
{
    /// <summary>
    /// Oracle suite for <see cref="LoadScheduleSectioner.Section"/>: the pure grouping behind the
    /// By-Room Load Schedule. Ordered rooms first (in Room Order), then resolved-but-unordered
    /// rooms alpha, then a single trailing "(No Room)"; within every section, natural circuit sort.
    /// </summary>
    public class LoadScheduleSectionerTests
    {
        private static LoadsCircuitModel Ckt(string number, string room, double va = 0) =>
            new LoadsCircuitModel { CircuitNumber = number, RoomName = room, ApparentLoadVA = va };

        private static List<string> Names(LoadSection s) => s.Circuits.Select(c => c.CircuitNumber).ToList();

        [Fact]
        public void OrderedRooms_FollowRoomOrder_NotInputOrAlpha()
        {
            var circuits = new[] { Ckt("1", "Bath"), Ckt("2", "Kitchen"), Ckt("3", "Foyer") };
            var roomOrder = new[] { "Foyer", "Kitchen", "Bath" };

            var sections = LoadScheduleSectioner.Section(circuits, roomOrder);

            Assert.Equal(new[] { "Foyer", "Kitchen", "Bath" }, sections.Select(s => s.RoomName));
            Assert.All(sections, s => Assert.False(s.IsNoRoom));
        }

        [Fact]
        public void WithinRoom_SortsByNaturalCircuitNumber()
        {
            var circuits = new[]
            {
                Ckt("10", "Kitchen"), Ckt("2", "Kitchen"), Ckt("1", "Kitchen"),
            };
            var sections = LoadScheduleSectioner.Section(circuits, new[] { "Kitchen" });

            Assert.Single(sections);
            Assert.Equal(new[] { "1", "2", "10" }, Names(sections[0])); // natural, not lexical (10<2)
        }

        [Fact]
        public void ResolvedButUnordered_ComeAfterOrdered_Alpha()
        {
            var circuits = new[]
            {
                Ckt("1", "Kitchen"),   // ordered
                Ckt("2", "Zebra Rm"),  // not in order
                Ckt("3", "Attic"),     // not in order
            };
            var roomOrder = new[] { "Kitchen" };

            var sections = LoadScheduleSectioner.Section(circuits, roomOrder);

            Assert.Equal(new[] { "Kitchen", "Attic", "Zebra Rm" }, sections.Select(s => s.RoomName));
        }

        [Fact]
        public void BlankOrNullRoom_GoesToTrailingNoRoomSection()
        {
            var circuits = new[]
            {
                Ckt("1", "Kitchen"),
                Ckt("2", ""),
                Ckt("3", "   "),
                Ckt("4", null!),
            };
            var sections = LoadScheduleSectioner.Section(circuits, new[] { "Kitchen" });

            Assert.Equal(2, sections.Count);
            var last = sections[^1];
            Assert.True(last.IsNoRoom);
            Assert.Equal(LoadScheduleSectioner.NoRoomLabel, last.RoomName);
            Assert.Equal(new[] { "2", "3", "4" }, Names(last));
        }

        [Fact]
        public void NoRoomSection_AbsentWhenEveryCircuitResolves()
        {
            var circuits = new[] { Ckt("1", "Kitchen"), Ckt("2", "Bath") };
            var sections = LoadScheduleSectioner.Section(circuits, new[] { "Kitchen", "Bath" });

            Assert.DoesNotContain(sections, s => s.IsNoRoom);
        }

        [Fact]
        public void UnnamedDmxPlaceholder_FilesUnderItsRoom_NotPinned()
        {
            var circuits = new[]
            {
                Ckt("2", "Kitchen"),
                Ckt("<...>", "Kitchen"), // DMX zone circuit — sorts naturally within its room
                Ckt("1", "Kitchen"),
            };
            var sections = LoadScheduleSectioner.Section(circuits, new[] { "Kitchen" });

            Assert.Single(sections);
            // "<...>" is not pinned to the bottom in By Room; it sorts by its number token.
            Assert.Contains("<...>", Names(sections[0]));
            Assert.Equal(3, sections[0].Circuits.Count);
        }

        [Fact]
        public void EmptyRoomOrder_AllRoomsFallToAlpha()
        {
            var circuits = new[] { Ckt("1", "Kitchen"), Ckt("2", "Attic"), Ckt("3", "Foyer") };

            var sections = LoadScheduleSectioner.Section(circuits, new string[0]);

            Assert.Equal(new[] { "Attic", "Foyer", "Kitchen" }, sections.Select(s => s.RoomName));
        }

        [Fact]
        public void RoomGrouping_IsCaseInsensitive()
        {
            var circuits = new[] { Ckt("1", "Kitchen"), Ckt("2", "KITCHEN"), Ckt("3", "kitchen") };
            var roomOrder = new[] { "kitchen" };

            var sections = LoadScheduleSectioner.Section(circuits, roomOrder);

            Assert.Single(sections);
            Assert.Equal(3, sections[0].Circuits.Count);
        }

        [Fact]
        public void EmptyInput_YieldsNoSections()
        {
            Assert.Empty(LoadScheduleSectioner.Section(new LoadsCircuitModel[0], new[] { "Kitchen" }));
            Assert.Empty(LoadScheduleSectioner.Section(null!, new[] { "Kitchen" }));
        }

        [Fact]
        public void OrderedBeforeUnordered_EvenWhenUnorderedAlphaPrecedes()
        {
            // "Attic" is alpha-first but unordered; "Kitchen" is ordered — ordered still wins.
            var circuits = new[] { Ckt("1", "Attic"), Ckt("2", "Kitchen") };
            var sections = LoadScheduleSectioner.Section(circuits, new[] { "Kitchen" });

            Assert.Equal(new[] { "Kitchen", "Attic" }, sections.Select(s => s.RoomName));
        }
    }
}
