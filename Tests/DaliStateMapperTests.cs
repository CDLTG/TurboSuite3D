using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dali;
using TurboSuite.Dali.Input;
using TurboSuite.Dali.Persistence;
using Xunit;

namespace TurboSuite.Tests.Dali
{
    /// <summary>
    /// Oracles for <see cref="DaliStateMapper.ToLoopDeclarations"/> — the persisted-loops → engine-input
    /// boundary. Pins the reconciliation rules it shares with the DMX mapper: order by declared Order, drop
    /// zones that no longer exist, single membership (first loop wins a contested zone), and skip a loop
    /// left with no live zones.
    /// </summary>
    public class DaliStateMapperTests
    {
        private static DaliLoopDto Loop(string name, int order, params string[] zones) =>
            new DaliLoopDto { LoopId = name, Name = name, Order = order, ZoneValues = zones.ToList() };

        [Fact]
        public void NullLoops_YieldEmpty()
        {
            var result = DaliStateMapper.ToLoopDeclarations(null, new[] { "A", "B" });
            Assert.Empty(result);
        }

        [Fact]
        public void LoopsAreOrderedByDeclaredOrder()
        {
            var loops = new[]
            {
                Loop("Second", 2, "B"),
                Loop("First", 1, "A"),
                Loop("Third", 3, "C"),
            };

            var result = DaliStateMapper.ToLoopDeclarations(loops, new[] { "A", "B", "C" });

            Assert.Equal(new[] { "First", "Second", "Third" }, result.Select(d => d.Name));
        }

        [Fact]
        public void ZonesNoLongerInTheModelAreDropped()
        {
            var loops = new[] { Loop("Kitchen", 1, "Live", "Renamed", "Deleted") };

            var result = DaliStateMapper.ToLoopDeclarations(loops, new[] { "Live" });

            var only = Assert.Single(result);
            Assert.Equal(new[] { "Live" }, only.ZoneNames);
        }

        [Fact]
        public void ContestedZoneSticksToTheFirstLoop()
        {
            var loops = new[]
            {
                Loop("First", 1, "Shared"),
                Loop("Second", 2, "Shared", "Own"),
            };

            var result = DaliStateMapper.ToLoopDeclarations(loops, new[] { "Shared", "Own" });

            Assert.Equal(new[] { "Shared" }, result[0].ZoneNames);
            Assert.Equal(new[] { "Own" }, result[1].ZoneNames);   // "Shared" already used → dropped here
        }

        [Fact]
        public void LoopLeftWithNoLiveZonesIsSkipped()
        {
            var loops = new[]
            {
                Loop("Empty", 1, "Gone"),          // its only zone no longer exists
                Loop("Real", 2, "Here"),
            };

            var result = DaliStateMapper.ToLoopDeclarations(loops, new[] { "Here" });

            var only = Assert.Single(result);
            Assert.Equal("Real", only.Name);
        }

        [Fact]
        public void ZoneMatchingIsCaseInsensitive()
        {
            var loops = new[] { Loop("Loop", 1, "kitchen") };

            var result = DaliStateMapper.ToLoopDeclarations(loops, new[] { "KITCHEN" });

            var only = Assert.Single(result);
            Assert.Equal(new[] { "kitchen" }, only.ZoneNames);
        }

        [Fact]
        public void NullZoneValuesOnADtoDoesNotThrow()
        {
            var loops = new[] { new DaliLoopDto { Name = "NoZones", Order = 1, ZoneValues = null! } };

            var result = DaliStateMapper.ToLoopDeclarations(loops, new[] { "A" });

            Assert.Empty(result);   // no live zones → skipped, not an exception
        }
    }
}
