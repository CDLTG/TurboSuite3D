using System.Collections.Generic;
using System.Linq;
using TurboSuite.Circuits;
using Xunit;

namespace TurboSuite.Tests.Circuits
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Oracle suite for CircuitRoomOverride (Core/Circuits/CircuitRoomOverride.cs).
    //  Pure logic behind the circuit-info dialog's Room Override field, shared by TurboWire and
    //  TurboDriver.
    //
    //  Two rules being pinned:
    //    (1) Untouched field == prefill  →  true no-op (ShouldPersist == false), never stamps a
    //        batch's prefill onto circuits with a different base room, never clears an existing
    //        override the user left alone.
    //    (2) Entered text == a circuit's own base room  →  CLEAR that circuit (blank), i.e. fall
    //        back to geometry rather than storing a redundant override.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    public class CircuitRoomOverrideTests
    {
        private static CircuitRoomState S(string uid, string baseRoom, string existing = "") =>
            new CircuitRoomState(uid, baseRoom, existing);

        // ── ComputePrefill ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Prefill_Empty_Batch_Is_Empty()
        {
            Assert.Equal("", CircuitRoomOverride.ComputePrefill(new List<CircuitRoomState>()));
        }

        [Fact]
        public void Prefill_Uses_Base_When_No_Override()
        {
            var circuits = new[] { S("a", "Kitchen") };
            Assert.Equal("Kitchen", CircuitRoomOverride.ComputePrefill(circuits));
        }

        [Fact]
        public void Prefill_Override_Wins_Over_Base()
        {
            var circuits = new[] { S("a", "Kitchen", "Pantry") };
            Assert.Equal("Pantry", CircuitRoomOverride.ComputePrefill(circuits));
        }

        [Fact]
        public void Prefill_Agreeing_Batch_Shows_Shared_Room()
        {
            var circuits = new[] { S("a", "Kitchen"), S("b", "Kitchen") };
            Assert.Equal("Kitchen", CircuitRoomOverride.ComputePrefill(circuits));
        }

        [Fact]
        public void Prefill_Agreement_Is_Case_Insensitive()
        {
            var circuits = new[] { S("a", "Kitchen"), S("b", "kitchen") };
            Assert.Equal("Kitchen", CircuitRoomOverride.ComputePrefill(circuits));
        }

        [Fact]
        public void Prefill_Disagreeing_Batch_Is_Varies()
        {
            var circuits = new[] { S("a", "Kitchen"), S("b", "Bath") };
            Assert.Equal(CircuitRoomOverride.VariesPlaceholder,
                CircuitRoomOverride.ComputePrefill(circuits));
        }

        [Fact]
        public void Prefill_Effective_Rooms_Reconcile_Across_Overrides()
        {
            // Base rooms differ, but overrides make the effective rooms agree.
            var circuits = new[] { S("a", "Kitchen", "Lobby"), S("b", "Bath", "Lobby") };
            Assert.Equal("Lobby", CircuitRoomOverride.ComputePrefill(circuits));
        }

        // ── Decide: no-op cases ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Decide_Untouched_Field_Is_NoOp()
        {
            var circuits = new[] { S("a", "Kitchen") };
            var prefill = CircuitRoomOverride.ComputePrefill(circuits);
            var d = CircuitRoomOverride.Decide(circuits, prefill, prefill);
            Assert.False(d.ShouldPersist);
        }

        [Fact]
        public void Decide_Untouched_Varies_Is_NoOp()
        {
            var circuits = new[] { S("a", "Kitchen"), S("b", "Bath") };
            var prefill = CircuitRoomOverride.ComputePrefill(circuits);
            var d = CircuitRoomOverride.Decide(circuits, prefill, prefill);
            Assert.False(d.ShouldPersist);
        }

        [Fact]
        public void Decide_Untouched_Preserves_Existing_Override_On_Divergent_Batch()
        {
            // The dangerous case: a saved override that the user did NOT touch must survive,
            // even when another circuit in the batch shows a different room (prefill = <varies>).
            var circuits = new[] { S("a", "Kitchen", "Pantry"), S("b", "Bath") };
            var prefill = CircuitRoomOverride.ComputePrefill(circuits); // <varies>
            var d = CircuitRoomOverride.Decide(circuits, prefill, prefill);
            Assert.False(d.ShouldPersist);
        }

        [Fact]
        public void Decide_Whitespace_Only_Edit_Equals_Prefill_Is_NoOp()
        {
            var circuits = new[] { S("a", "Kitchen") };
            var d = CircuitRoomOverride.Decide(circuits, "Kitchen", "  Kitchen  ");
            Assert.False(d.ShouldPersist);
        }

        // ── Decide: writing overrides ───────────────────────────────────────────────────────────────

        [Fact]
        public void Decide_Typed_Room_Writes_Override_For_All_Circuits()
        {
            var circuits = new[] { S("a", "Kitchen"), S("b", "Bath") };
            var prefill = CircuitRoomOverride.ComputePrefill(circuits); // <varies>
            var d = CircuitRoomOverride.Decide(circuits, prefill, "Lobby");
            Assert.True(d.ShouldPersist);
            Assert.Equal("Lobby", d.Changes["a"]);
            Assert.Equal("Lobby", d.Changes["b"]);
        }

        [Fact]
        public void Decide_Typed_Room_Equal_To_Base_Clears_That_Circuit()
        {
            // Enter "Kitchen": circuit a's base IS Kitchen → clear (blank); circuit b's base is
            // Bath → store the override.
            var circuits = new[] { S("a", "Kitchen"), S("b", "Bath") };
            var prefill = CircuitRoomOverride.ComputePrefill(circuits); // <varies>
            var d = CircuitRoomOverride.Decide(circuits, prefill, "Kitchen");
            Assert.True(d.ShouldPersist);
            Assert.Equal("", d.Changes["a"]);
            Assert.Equal("Kitchen", d.Changes["b"]);
        }

        [Fact]
        public void Decide_Cleared_Field_Removes_Existing_Override()
        {
            // Override was "Pantry"; user blanks the field. Base is Kitchen → clear back to geometry.
            var circuits = new[] { S("a", "Kitchen", "Pantry") };
            var prefill = CircuitRoomOverride.ComputePrefill(circuits); // Pantry
            var d = CircuitRoomOverride.Decide(circuits, prefill, "");
            Assert.True(d.ShouldPersist);
            Assert.Equal("", d.Changes["a"]);
        }

        [Fact]
        public void Decide_Change_That_Only_Reasserts_Base_On_Clean_Circuit_Does_Not_Persist()
        {
            // Prefill was blank (no base room resolved, no override). User types then retypes
            // the base... here base is blank, user types "" — but that equals prefill, so no-op.
            // The genuine "nothing to persist" path: entered equals base for the only circuit and
            // there was no existing override to clear.
            var circuits = new[] { S("a", "Kitchen") };
            // Prefill is "Kitchen"; user edits to something then back is covered elsewhere. Here
            // simulate a changed field ("") that resolves to clearing a circuit with no override.
            var d = CircuitRoomOverride.Decide(circuits, "Kitchen", "");
            // entered "" != prefill "Kitchen" → changed; but "" is not an override and there is no
            // existing override to clear → nothing to persist.
            Assert.False(d.ShouldPersist);
            Assert.Equal("", d.Changes["a"]);
        }
    }
}
