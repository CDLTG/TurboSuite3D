using System.Collections.Generic;
using System.Linq;
using TurboSuite.Dmx;
using TurboSuite.Dmx.Input;
using TurboSuite.Dmx.Persistence;
using Xunit;

namespace TurboSuite.Tests.Dmx
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for DmxHeadlessSolve — TurboDMX's solve run from the persisted design alone,
    //  with no window open. This is what puts a QSE-CI-DMX quantity on a purchasing document, so a
    //  wrong answer here is a wrong order.
    //
    //  For me (Claude): the load-bearing property is that this NEVER THROWS. Every engine refusal
    //  must come back as a Diagnostic string. A test that expects an exception out of Solve() is
    //  asserting the bug this class exists to prevent — a DMX design mid-edit taking down the whole
    //  control BOM PDF.
    //
    //  The second property: "no DMX" and "DMX that won't solve" are different answers. The first is
    //  silent (most jobs have no DMX); the second carries a reason. Collapsing them either spams
    //  every job with a warning or hides real missing hardware.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    public class DmxHeadlessSolveTests
    {
        private static DmxFixtureReading Tape(string zone, int channels = 4,
                                              double lengthFt = 5.0, double wattsPerFt = 1.0)
            => new DmxFixtureReading
            {
                ElementId = _nextId++,
                ControlZone = zone,
                Channels = channels,
                LengthFt = lengthFt,
                WattsPerFt = wattsPerFt,
                MaxPerBundle = 1,
                TypeMark = "TAPE"
            };

        private static long _nextId = 1;

        private static readonly DmxDecoderCandidate Decoder = new DmxDecoderCandidate
        {
            TypeId = "dec-1", Name = "4ch", MaxOutputs = 4, MaxAmpsPerOutput = 10, MaxWatts = 960
        };

        private static readonly DmxDriverCandidate Driver = new DmxDriverCandidate
        {
            TypeId = "drv-1", Name = "ME", TypeMark = "ME",
            RatedWatts = 600, OperatingVolts = 24, DeratingFactorRaw = 0.85
        };

        private static DmxModelSnapshot Snapshot(params DmxFixtureReading[] fixtures)
            => new DmxModelSnapshot
            {
                Fixtures = fixtures,
                DecoderCandidates = new[] { Decoder },
                DriverCandidates = new[] { Driver }
            };

        /// <summary>A fully curated job — the shape a designer leaves behind after using TurboDMX.</summary>
        private static DmxModuleState CuratedState()
            => new DmxModuleState
            {
                Settings = new DmxSettingsDto
                {
                    Profile = "Lutron",
                    DecoderTypeIds = new List<string> { Decoder.TypeId },
                    DriverTypeIds = new List<string> { Driver.TypeId }
                }
            };

        [Fact]
        public void SolvesACuratedJobIntoInterfaces()
        {
            var result = DmxHeadlessSolve.Solve(Snapshot(Tape("COVE 1"), Tape("COVE 2")), CuratedState());

            Assert.Null(result.Diagnostic);
            Assert.NotNull(result.Bill);
            Assert.Equal(1, result.Bill!.InterfaceCount);   // 8 channels, well under the 32 ceiling
            Assert.Equal(new[] { "COVE 1", "COVE 2" }, result.ZoneNames);
        }

        /// <summary>The common case by far: no DMX in the job. Silent — a diagnostic here would put a
        /// warning on every non-DMX BOM in the office.</summary>
        [Fact]
        public void NoDmxFixturesIsACleanNothing()
        {
            var result = DmxHeadlessSolve.Solve(Snapshot(), CuratedState());

            Assert.Null(result.Diagnostic);
            Assert.Null(result.Bill);
        }

        /// <summary>DMX tape in the model with nothing zoned is NOT a clean nothing. Their circuits are
        /// excluded from panel allocation because a subsystem owns them, so without this the job orders
        /// no interfaces and nothing anywhere says why.</summary>
        [Fact]
        public void UnzonedFixturesAreReportedNotSilent()
        {
            var result = DmxHeadlessSolve.Solve(Snapshot(Tape(""), Tape("")), CuratedState());

            Assert.Null(result.Bill);
            Assert.Contains("Control Zone", result.Diagnostic);
            Assert.Contains("2 fixtures have", result.Diagnostic);
        }

        [Fact]
        public void UnzonedFixtureDiagnosticReadsSingularForOne()
            => Assert.Contains("1 fixture has",
                DmxHeadlessSolve.Solve(Snapshot(Tape("")), CuratedState()).Diagnostic);

        /// <summary>The dangerous middle case: some tape zoned, some not. The solve is complete for
        /// what it saw and the count is simply too low — so the parts ship WITH the caveat, because
        /// withholding either half would mislead.</summary>
        [Fact]
        public void PartiallyZonedJobSolvesWithACaveat()
        {
            var result = DmxHeadlessSolve.Solve(Snapshot(Tape("COVE 1"), Tape("")), CuratedState());

            Assert.NotNull(result.Bill);
            Assert.Equal(1, result.Bill!.InterfaceCount);
            Assert.Contains("not counted", result.Diagnostic);
        }

        /// <summary>A fully zoned job carries no caveat — the warning must not become background noise
        /// that users learn to scroll past.</summary>
        [Fact]
        public void FullyZonedJobCarriesNoCaveat()
            => Assert.Null(DmxHeadlessSolve.Solve(Snapshot(Tape("COVE 1")), CuratedState()).Diagnostic);

        /// <summary>A zero-channel DMX fixture is an authoring error with nothing orderable, so it must NOT
        /// warn the BOM — a purchasing-document line has to be something you can order. It is surfaced in
        /// TurboDMX (the window summary) instead. Here it is the only DMX content: a clean nothing on the BOM.</summary>
        [Fact]
        public void ZeroChannelFixturesDoNotWarnTheBom()
        {
            var result = DmxHeadlessSolve.Solve(Snapshot(Tape("COVE 1", channels: 0)), CuratedState());

            Assert.Null(result.Bill);
            Assert.Null(result.Diagnostic);   // no orderable hardware ⇒ nothing for the BOM to say
        }

        /// <summary>Zoned tape plus a zero-channel straggler: the bill stands and carries NO caveat — the
        /// straggler orders nothing, so it never undercounts. (Contrast an UNZONED fixture, which does.)</summary>
        [Fact]
        public void ZeroChannelFixtureOnAnOtherwiseSolvedJobIsSilentOnTheBom()
        {
            var result = DmxHeadlessSolve.Solve(
                Snapshot(Tape("COVE 1"), Tape("COVE 2", channels: 0)), CuratedState());

            Assert.NotNull(result.Bill);
            Assert.Equal(1, result.Bill!.InterfaceCount);
            Assert.Null(result.Diagnostic);
        }

        /// <summary>But once zones exist, an uncurated kit is a real gap: there IS hardware here and
        /// nobody can price it. The zone names survive so the reason has context.</summary>
        [Fact]
        public void ZonesWithoutADecoderKitAreReported()
        {
            var state = CuratedState();
            state.Settings.DecoderTypeIds.Clear();

            var result = DmxHeadlessSolve.Solve(Snapshot(Tape("COVE 1")), state);

            Assert.Null(result.Bill);
            Assert.Contains("decoder", result.Diagnostic);
            Assert.Equal(new[] { "COVE 1" }, result.ZoneNames);
        }

        [Fact]
        public void ZonesWithoutADriverKitAreReported()
        {
            var state = CuratedState();
            state.Settings.DriverTypeIds.Clear();

            var result = DmxHeadlessSolve.Solve(Snapshot(Tape("COVE 1")), state);

            Assert.Null(result.Bill);
            Assert.Contains("driver", result.Diagnostic);
        }

        /// <summary>An engine hard stop comes back as a message, not an exception. Here a zone needs
        /// more channels than any curated decoder has outputs — the contract abort.</summary>
        [Fact]
        public void EngineHardStopBecomesADiagnostic()
        {
            var result = DmxHeadlessSolve.Solve(Snapshot(Tape("RGBATW", channels: 6)), CuratedState());

            Assert.Null(result.Bill);
            Assert.False(string.IsNullOrWhiteSpace(result.Diagnostic));
        }

        /// <summary>Null inputs are the never-opened-TurboDMX case, which every existing job is.</summary>
        [Fact]
        public void NullInputsAreACleanNothing()
        {
            var result = DmxHeadlessSolve.Solve(null, null);

            Assert.Null(result.Bill);
            Assert.Null(result.Diagnostic);
            Assert.Empty(result.ZoneNames);
        }

        /// <summary>Declared loops are honored — that is the whole point of declaring them, and it is
        /// also what makes interfaces partly filled and the 16-per-link cap matter.</summary>
        [Fact]
        public void DeclaredLoopsSplitInterfaces()
        {
            var state = CuratedState();
            state.Loops = new List<DmxLoopDto>
            {
                new DmxLoopDto { Name = "L1", Order = 0, ZoneValues = new List<string> { "COVE 1" } },
                new DmxLoopDto { Name = "L2", Order = 1, ZoneValues = new List<string> { "COVE 2" } }
            };

            var result = DmxHeadlessSolve.Solve(Snapshot(Tape("COVE 1"), Tape("COVE 2")), state);

            // Auto-packed these two zones share one interface; declared into separate loops they cannot.
            Assert.Equal(2, result.Bill!.InterfaceCount);
        }
    }

    /// <summary>
    /// The persisted-state → engine-input mapping. Shared by the TurboDMX window and the headless
    /// solve, which is the point: the window and a BOM built without it must read the same saved job
    /// the same way.
    /// </summary>
    public class DmxStateMapperTests
    {
        [Fact]
        public void UnknownOrMissingProfileFallsBackToLutron()
        {
            Assert.Same(DmxProfile.Lutron, DmxStateMapper.ToProfile(null));
            Assert.Same(DmxProfile.Lutron, DmxStateMapper.ToProfile(new DmxSettingsDto { Profile = "nope" }));
            Assert.Same(DmxProfile.Crestron, DmxStateMapper.ToProfile(new DmxSettingsDto { Profile = "crestron" }));
        }

        /// <summary>A never-saved DTO must round-trip to the window's own defaults, not to zeros —
        /// applying it is meant to be a no-op, and a zeroed breaker cap would fail every solve.</summary>
        [Fact]
        public void FreshDtoMapsToDefaultsNotZeros()
        {
            var mapped = DmxStateMapper.ToJobSettings(new DmxSettingsDto());
            var defaults = new DmxJobSettings();

            Assert.Equal(defaults.SystemVolts, mapped.SystemVolts);
            Assert.Equal(defaults.BreakerAmps, mapped.BreakerAmps);
            Assert.Equal(defaults.FeedVolts, mapped.FeedVolts);
            Assert.Equal(defaults.BreakerContinuousDerate, mapped.BreakerContinuousDerate);
            Assert.Equal(defaults.BreakerBasis, mapped.BreakerBasis);
        }

        [Fact]
        public void UnparseableBreakerBasisFallsBackToTheSafeNameplateDefault()
            => Assert.Equal(BreakerBasis.DriverRating,
                DmxStateMapper.ToJobSettings(new DmxSettingsDto { BreakerBasis = "garbage" }).BreakerBasis);

        /// <summary>Loops reconcile against the zones that exist NOW: a renamed or deleted zone drops
        /// out rather than failing the solve.</summary>
        [Fact]
        public void LoopsDropZonesThatNoLongerExist()
        {
            var loops = new[]
            {
                new DmxLoopDto { Name = "L1", Order = 0, ZoneValues = new List<string> { "A", "GONE" } }
            };

            var declared = DmxStateMapper.ToLoopDeclarations(loops, new[] { "A", "B" });

            Assert.Equal(new[] { "A" }, Assert.Single(declared).ZoneNames);
        }

        /// <summary>Single membership: a zone named by two loops sticks to the first, in Order.</summary>
        [Fact]
        public void ZoneClaimedTwiceSticksToTheFirstLoop()
        {
            var loops = new[]
            {
                new DmxLoopDto { Name = "second", Order = 1, ZoneValues = new List<string> { "A" } },
                new DmxLoopDto { Name = "first",  Order = 0, ZoneValues = new List<string> { "A" } }
            };

            var declared = DmxStateMapper.ToLoopDeclarations(loops, new[] { "A" });

            Assert.Equal("first", Assert.Single(declared).Name);
        }

        /// <summary>A loop left with no surviving zones is skipped — an empty declaration would claim
        /// an interface, and an interface is a part on a purchasing document.</summary>
        [Fact]
        public void EmptiedLoopsAreSkipped()
        {
            var loops = new[]
            {
                new DmxLoopDto { Name = "L1", Order = 0, ZoneValues = new List<string> { "GONE" } }
            };

            Assert.Empty(DmxStateMapper.ToLoopDeclarations(loops, new[] { "A" }));
        }

        /// <summary>Curation is by stable type id, and a never-curated job yields nothing rather than
        /// silently defaulting to every discovered type — guessing a kit would price the wrong parts.</summary>
        [Fact]
        public void CuratedKitsFilterByTypeId()
        {
            var discovered = new[]
            {
                new DmxDecoderCandidate { TypeId = "a", Name = "A" },
                new DmxDecoderCandidate { TypeId = "b", Name = "B" }
            };

            var dto = new DmxSettingsDto { DecoderTypeIds = new List<string> { "b" } };

            Assert.Equal("B", Assert.Single(DmxStateMapper.ToCuratedDecoders(discovered, dto)).Name);
            Assert.Empty(DmxStateMapper.ToCuratedDecoders(discovered, new DmxSettingsDto()));
        }
    }
}
