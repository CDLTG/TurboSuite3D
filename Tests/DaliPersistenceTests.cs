using System.Collections.Generic;
using System.Text.Json;
using TurboSuite.Dali.Persistence;
using Xunit;

namespace TurboSuite.Tests.Dali
{
    /// <summary>
    /// Round-trip oracles for the DALI ExtensibleStorage payload shape. The actual ES read/write lives in
    /// the Revit-coupled <c>DaliStorageService</c> (manual-tested, like DMX); what is at risk in pure land
    /// is the DTO's JSON serializability and the tolerant-read contract the service relies on, so those are
    /// pinned here against the same serializer options the service uses.
    /// </summary>
    public class DaliPersistenceTests
    {
        // Mirrors DaliStorageService.JsonOptions (compact write, case-insensitive read).
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        private static DaliModuleState RoundTrip(DaliModuleState state) =>
            JsonSerializer.Deserialize<DaliModuleState>(JsonSerializer.Serialize(state, Options), Options);

        [Fact]
        public void FreshStateHasSensibleDefaults()
        {
            var state = new DaliModuleState();
            Assert.Equal(2, state.PayloadVersion);   // v2: DaliLoopDto.AssignedZone (Phase 3e)
            Assert.Empty(state.Loops);
        }

        [Fact]
        public void DeclaredLoopsRoundTripIntact()
        {
            var state = new DaliModuleState
            {
                PayloadVersion = 1,
                Loops = new List<DaliLoopDto>
                {
                    new DaliLoopDto { LoopId = "l1", Name = "North", Order = 1, AssignedZone = 4,
                                      ZoneValues = new List<string> { "Kitchen", "Hall" } },
                    new DaliLoopDto { LoopId = "l2", Name = "South", Order = 2,
                                      ZoneValues = new List<string> { "Bath" } },
                }
            };

            var back = RoundTrip(state);

            Assert.Equal(2, back.Loops.Count);
            Assert.Equal("North", back.Loops[0].Name);
            Assert.Equal("l1", back.Loops[0].LoopId);
            Assert.Equal(1, back.Loops[0].Order);
            Assert.Equal(4, back.Loops[0].AssignedZone);
            Assert.Equal(new[] { "Kitchen", "Hall" }, back.Loops[0].ZoneValues);
            Assert.Equal("South", back.Loops[1].Name);
            Assert.Equal(0, back.Loops[1].AssignedZone);   // never assigned ⇒ unassigned
            Assert.Equal(new[] { "Bath" }, back.Loops[1].ZoneValues);
        }

        [Fact]
        public void V1Payload_DefaultsAssignedZoneToUnassigned()
        {
            // A loop persisted before Phase 3e carries no assignedZone; it must read as 0 (unassigned),
            // so an older job's loops are ordered-but-warned rather than mis-placed into ZONE 0.
            const string json =
                "{\"payloadVersion\":1,\"loops\":[{\"loopId\":\"x\",\"name\":\"L\",\"order\":1," +
                "\"zoneValues\":[\"Z\"]}]}";

            var state = JsonSerializer.Deserialize<DaliModuleState>(json, Options);

            Assert.Equal(0, Assert.Single(state.Loops).AssignedZone);
        }

        [Fact]
        public void ReadIsCaseInsensitiveOnPropertyNames()
        {
            // A hand-shaped payload with camelCase keys must still bind (the service reads tolerant).
            const string json =
                "{\"payloadVersion\":1,\"loops\":[{\"loopId\":\"x\",\"name\":\"L\",\"order\":3," +
                "\"zoneValues\":[\"Z\"]}]}";

            var state = JsonSerializer.Deserialize<DaliModuleState>(json, Options);

            var loop = Assert.Single(state.Loops);
            Assert.Equal("L", loop.Name);
            Assert.Equal(3, loop.Order);
            Assert.Equal(new[] { "Z" }, loop.ZoneValues);
        }

        [Fact]
        public void UnknownFieldsFromAFuturePayloadAreIgnored()
        {
            // Forward tolerance: a newer build's extra field must not break an older reader.
            const string json =
                "{\"payloadVersion\":2,\"loops\":[],\"somethingNew\":{\"a\":1},\"futureFlag\":true}";

            var state = JsonSerializer.Deserialize<DaliModuleState>(json, Options);

            Assert.Equal(2, state.PayloadVersion);
            Assert.Empty(state.Loops);
        }
    }
}
