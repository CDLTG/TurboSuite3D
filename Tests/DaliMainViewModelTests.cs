using System;
using System.Collections.Generic;
using System.Linq;
using TurboSuite.Abstractions;
using TurboSuite.Dali.Addressing;
using TurboSuite.Dali.Input;
using TurboSuite.Dali.Overlay;
using TurboSuite.Dali.Persistence;
using TurboSuite.Dali.Services;
using TurboSuite.Dali.ViewModels;
using Xunit;

namespace TurboSuite.Tests.Dali
{
    /// <summary>
    /// Oracles for <see cref="DaliMainViewModel"/>'s addressing + numbering-lock lifecycle. Revit-free: the
    /// model read, param write, and snapshot persistence are all faked, so the lock-aware reconcile, the
    /// Lock/Re-lock/Unlock transitions, and the merge-preserving snapshot persistence are pinned directly.
    /// </summary>
    public class DaliMainViewModelTests
    {
        // ── Fakes ─────────────────────────────────────────────────────────────────────────────────────
        private sealed class SyncWorkQueue : IRevitWorkQueue
        {
            public void Enqueue(Func<object> work, Action<object> onComplete) => onComplete?.Invoke(work());
        }

        private sealed class CapturingStore : IDaliLoopStore
        {
            public DaliSnapshotDto? LastSnapshot;
            public int SnapshotSaveCount;
            public int LoopSaveCount;
            public void Save(DaliModuleState state) { LoopSaveCount++; }
            public void SaveSnapshot(DaliSnapshotDto? snapshot) { LastSnapshot = snapshot; SnapshotSaveCount++; }
        }

        private sealed class FakeReader : IDaliModelReader
        {
            public List<DaliUnitReading> Units = new List<DaliUnitReading>();
            public int Reads;
            public DaliModelSnapshot Read() { Reads++; return new DaliModelSnapshot(Units.ToList(), 0); }
        }

        private sealed class FakeWriter : IDaliAddressWriter
        {
            public IReadOnlyDictionary<string, string> LastWrite = new Dictionary<string, string>();
            public int Writes;
            public string Write(IReadOnlyDictionary<string, string> m) { LastWrite = m; Writes++; return ""; }
        }

        private sealed class FakeZoneColor : IDaliZoneColorService
        {
            public string Apply(IReadOnlyDictionary<string, DaliColor> zoneColors) => "";
            public string Revert() => "";
        }

        // ── Builders ──────────────────────────────────────────────────────────────────────────────────
        // A single-unit (downlight) circuit: unit key == circuit key, at (x,y).
        private static DaliUnitReading Circuit(string key, string zone, double x, double y) =>
            new DaliUnitReading(key, key, DaliUnitKind.Downlight, 0, zone, new DaliPoint(x, y));

        /// <summary>One loop "loopA" grouping Control Zone "Kitchen", plus a fresh main VM over the given
        /// model units. <paramref name="savedSnapshot"/> seeds the persisted lock baseline (null = unlocked).</summary>
        private static DaliMainViewModel Build(
            IEnumerable<DaliUnitReading> units,
            out FakeWriter writer,
            out CapturingStore store,
            DaliSnapshotDto? savedSnapshot = null,
            Func<string, bool>? confirm = null)
        {
            var wq = new SyncWorkQueue();
            store = new CapturingStore();

            var saved = new DaliModuleState
            {
                PayloadVersion = savedSnapshot != null ? 4 : 2,
                Snapshot = savedSnapshot,
                Loops = new List<DaliLoopDto>
                {
                    new DaliLoopDto { LoopId = "loopA", Name = "Loop 1", Order = 0,
                                      AssignedZone = 1, ZoneValues = new List<string> { "Kitchen" } },
                },
            };
            var zones = new List<DaliZoneItemViewModel> { new DaliZoneItemViewModel("Kitchen", 2) };
            var tab = new DaliTabViewModel(zones, new List<int> { 1 }, saved, wq, store);

            var reader = new FakeReader { Units = units.ToList() };
            writer = new FakeWriter();
            return new DaliMainViewModel(tab, wq, reader, writer, new FakeZoneColor(), store,
                                         inputProvider: null, saved: saved, confirm: confirm ?? (_ => true));
        }

        // ── Unlocked write ──────────────────────────────────────────────────────────────────────────
        [Fact]
        public void UnlockedWrite_AssignsFreshAddresses_NoReviews()
        {
            var vm = Build(new[] { Circuit("c1", "Kitchen", 0, 10), Circuit("c2", "Kitchen", 0, 0) },
                           out var writer, out _);

            vm.WriteAddressesCommand.Execute(null);

            Assert.Equal(1, writer.Writes);
            Assert.Equal("L1-00", writer.LastWrite["c1"]);   // NW-first: higher Y seeds -00 (zero-based)
            Assert.Equal("L1-01", writer.LastWrite["c2"]);
            Assert.False(vm.HasReviews);
            Assert.False(vm.IsLocked);
        }

        // ── Lock captures + persists the baseline ─────────────────────────────────────────────────────
        [Fact]
        public void Lock_WritesAddresses_CapturesSnapshot_AndPersistsIt()
        {
            var vm = Build(new[] { Circuit("c1", "Kitchen", 0, 10), Circuit("c2", "Kitchen", 0, 0) },
                           out var writer, out var store);

            vm.LockCommand.Execute(null);

            Assert.True(vm.IsLocked);
            Assert.Equal("Re-lock", vm.LockButtonText);
            Assert.Equal(1, store.SnapshotSaveCount);
            Assert.NotNull(store.LastSnapshot);
            Assert.Equal("Locked", store.LastSnapshot!.NumberingState);
            Assert.Equal(2, store.LastSnapshot.Units.Count);
            Assert.Contains(store.LastSnapshot.Units, c => c.UnitKey == "c1" && c.LoadNumber == 0);
            Assert.Contains(store.LastSnapshot.Units, c => c.UnitKey == "c2" && c.LoadNumber == 1);
            Assert.Equal(1, writer.Writes);   // lock stamps the model so it matches the baseline
        }

        // ── Locked write: a deleted circuit's address is retired (REVIEW), not reused ──────────────────
        [Fact]
        public void LockedWrite_DeletedCircuit_RetiresAddress_AndReviews()
        {
            var baseline = new DaliSnapshotDto
            {
                NumberingState = "Locked",
                Loops = { new DaliSnapshotLoopDto { LoopId = "loopA", LoopNumber = 1 } },
                Units =
                {
                    new DaliSnapshotUnitDto { UnitKey = "c1", LoopId = "loopA", LoopNumber = 1, LoadNumber = 0, Zone = "Kitchen" },
                    new DaliSnapshotUnitDto { UnitKey = "c2", LoopId = "loopA", LoopNumber = 1, LoadNumber = 1, Zone = "Kitchen" },
                },
            };
            // c2 is gone from the model.
            var vm = Build(new[] { Circuit("c1", "Kitchen", 0, 10) }, out var writer, out _,
                           savedSnapshot: baseline);

            vm.WriteAddressesCommand.Execute(null);

            Assert.True(vm.IsLocked);
            Assert.Equal("L1-00", writer.LastWrite["c1"]);   // kept in place
            Assert.False(writer.LastWrite.ContainsKey("c2"));
            Assert.True(vm.HasReviews);
            Assert.Contains(vm.Reviews, r => r.Contains("L1-01") && r.Contains("retired"));
        }

        // ── Locked write: a new circuit appends past the high-water (no reuse), silently ───────────────
        [Fact]
        public void LockedWrite_NewCircuit_AppendsPastHighWater()
        {
            var baseline = new DaliSnapshotDto
            {
                NumberingState = "Locked",
                Loops = { new DaliSnapshotLoopDto { LoopId = "loopA", LoopNumber = 1 } },
                Units =
                {
                    new DaliSnapshotUnitDto { UnitKey = "c1", LoopId = "loopA", LoopNumber = 1, LoadNumber = 0, Zone = "Kitchen" },
                    new DaliSnapshotUnitDto { UnitKey = "c2", LoopId = "loopA", LoopNumber = 1, LoadNumber = 1, Zone = "Kitchen" },
                },
            };
            // c3 is new, and spatially the most-NW (highest Y) — a fresh walk would seed it -00, but locked it appends.
            var vm = Build(new[]
            {
                Circuit("c1", "Kitchen", 0, 10),
                Circuit("c2", "Kitchen", 0, 0),
                Circuit("c3", "Kitchen", 0, 99),
            }, out var writer, out _, savedSnapshot: baseline);

            vm.WriteAddressesCommand.Execute(null);

            Assert.Equal("L1-00", writer.LastWrite["c1"]);
            Assert.Equal("L1-01", writer.LastWrite["c2"]);
            Assert.Equal("L1-02", writer.LastWrite["c3"]);   // appended, not seeded to -00
            Assert.False(vm.HasReviews);
        }

        // ── Unlock discards the baseline, clears reviews, needs confirmation ───────────────────────────
        [Fact]
        public void Unlock_ClearsBaseline_AndReviews()
        {
            var baseline = new DaliSnapshotDto
            {
                NumberingState = "Locked",
                Loops = { new DaliSnapshotLoopDto { LoopId = "loopA", LoopNumber = 1 } },
                Units = { new DaliSnapshotUnitDto { UnitKey = "c1", LoopId = "loopA", LoopNumber = 1, LoadNumber = 0, Zone = "Kitchen" } },
            };
            var vm = Build(new[] { Circuit("c1", "Kitchen", 0, 0) }, out _, out var store,
                           savedSnapshot: baseline);
            Assert.True(vm.IsLocked);

            vm.UnlockCommand.Execute(null);

            Assert.False(vm.IsLocked);
            Assert.Equal("Lock", vm.LockButtonText);
            Assert.Equal("Unlocked", store.LastSnapshot!.NumberingState);
            Assert.False(vm.HasReviews);
        }

        [Fact]
        public void Unlock_Declined_KeepsLock()
        {
            var baseline = new DaliSnapshotDto { NumberingState = "Locked" };
            var vm = Build(new[] { Circuit("c1", "Kitchen", 0, 0) }, out _, out var store,
                           savedSnapshot: baseline, confirm: _ => false);

            vm.UnlockCommand.Execute(null);

            Assert.True(vm.IsLocked);
            Assert.Equal(0, store.SnapshotSaveCount);   // nothing persisted when the gate is declined
        }
    }
}
