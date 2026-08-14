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
            JsonSerializer.Deserialize<DaliModuleState>(JsonSerializer.Serialize(state, Options), Options)!;

        [Fact]
        public void FreshStateHasSensibleDefaults()
        {
            var state = new DaliModuleState();
            Assert.Equal(2, state.PayloadVersion);   // v2: DaliLoopDto.AssignedZone
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
            // A loop persisted before the AssignedZone field carries none; it must read as 0 (unassigned),
            // so an older job's loops are ordered-but-warned rather than mis-placed into ZONE 0.
            const string json =
                "{\"payloadVersion\":1,\"loops\":[{\"loopId\":\"x\",\"name\":\"L\",\"order\":1," +
                "\"zoneValues\":[\"Z\"]}]}";

            var state = JsonSerializer.Deserialize<DaliModuleState>(json, Options)!;

            Assert.Equal(0, Assert.Single(state.Loops).AssignedZone);
        }

        [Fact]
        public void ReadIsCaseInsensitiveOnPropertyNames()
        {
            // A hand-shaped payload with camelCase keys must still bind (the service reads tolerant).
            const string json =
                "{\"payloadVersion\":1,\"loops\":[{\"loopId\":\"x\",\"name\":\"L\",\"order\":3," +
                "\"zoneValues\":[\"Z\"]}]}";

            var state = JsonSerializer.Deserialize<DaliModuleState>(json, Options)!;

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

            var state = JsonSerializer.Deserialize<DaliModuleState>(json, Options)!;

            Assert.Equal(2, state.PayloadVersion);
            Assert.Empty(state.Loops);
        }

        // ── v3: the addressing lock baseline (TurboDALI) ────────────────────────────────────────────────

        [Fact]
        public void V3Snapshot_RoundTripsIntact()
        {
            var state = new DaliModuleState
            {
                PayloadVersion = 3,
                Loops = new List<DaliLoopDto>
                {
                    new DaliLoopDto { LoopId = "l1", Name = "Kitchen", Order = 1, AssignedZone = 3,
                                      ZoneValues = new List<string> { "Kitchen" } },
                },
                Snapshot = new DaliSnapshotDto
                {
                    NumberingState = "Locked",
                    Loops = new List<DaliSnapshotLoopDto>
                    {
                        new DaliSnapshotLoopDto { LoopId = "l1", LoopNumber = 1 },
                    },
                    Circuits = new List<DaliSnapshotCircuitDto>
                    {
                        new DaliSnapshotCircuitDto { CircuitKey = "u-1", LoopId = "l1",
                                                     LoopNumber = 1, LoadNumber = 2, Zone = "Kitchen" },
                    },
                },
            };

            var back = RoundTrip(state);

            Assert.Equal(3, back.PayloadVersion);
            Assert.NotNull(back.Snapshot);
            Assert.Equal("Locked", back.Snapshot!.NumberingState);
            var loop = Assert.Single(back.Snapshot.Loops);
            Assert.Equal("l1", loop.LoopId);
            Assert.Equal(1, loop.LoopNumber);
            var ckt = Assert.Single(back.Snapshot.Circuits);
            Assert.Equal("u-1", ckt.CircuitKey);
            Assert.Equal(2, ckt.LoadNumber);
            Assert.Equal("Kitchen", ckt.Zone);
        }

        [Fact]
        public void V2Payload_HasNullSnapshot()
        {
            // A pre-TurboDALI payload carries no snapshot ⇒ null = Unlocked/unaddressed, the safe default.
            const string json =
                "{\"payloadVersion\":2,\"loops\":[{\"loopId\":\"x\",\"name\":\"L\",\"order\":1," +
                "\"zoneValues\":[\"Z\"],\"assignedZone\":4}]}";

            var state = JsonSerializer.Deserialize<DaliModuleState>(json, Options)!;

            Assert.Null(state.Snapshot);
            Assert.Equal(4, Assert.Single(state.Loops).AssignedZone);
        }

        [Fact]
        public void V3Payload_DegradesCleanlyForAReaderThatIgnoresTheSnapshot()
        {
            // An old v2 reader seeing a v3 payload must still get its loops intact — the snapshot field it
            // doesn't consume is simply ignored, never corrupting the loops it does need. We characterize this
            // with tolerant read (unknown/unused fields dropped, loops preserved).
            const string json =
                "{\"payloadVersion\":3,\"loops\":[{\"loopId\":\"l1\",\"name\":\"Kitchen\",\"order\":1," +
                "\"zoneValues\":[\"Kitchen\"],\"assignedZone\":3}]," +
                "\"snapshot\":{\"numberingState\":\"Locked\"," +
                "\"loops\":[{\"loopId\":\"l1\",\"loopNumber\":1}]," +
                "\"circuits\":[{\"circuitKey\":\"u-1\",\"loopId\":\"l1\",\"loopNumber\":1," +
                "\"loadNumber\":2,\"zone\":\"Kitchen\"}]}}";

            var state = JsonSerializer.Deserialize<DaliModuleState>(json, Options)!;

            var loop = Assert.Single(state.Loops);
            Assert.Equal("Kitchen", loop.Name);
            Assert.Equal(3, loop.AssignedZone);
            Assert.Equal(new[] { "Kitchen" }, loop.ZoneValues);
        }
    }
}
