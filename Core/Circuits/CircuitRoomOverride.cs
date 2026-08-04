using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Circuits
{
    /// <summary>
    /// The live room state of one circuit as the circuit-info dialog sees it: the
    /// geometry-resolved <see cref="BaseRoom"/> (linked Room / region fallback) and the
    /// user's saved <see cref="ExistingOverride"/>, if any. The base room is never stored
    /// — it is recomputed each run — so a blank override always means "fall back to
    /// geometry."
    /// </summary>
    public sealed class CircuitRoomState
    {
        public string UniqueId { get; }
        public string BaseRoom { get; }
        public string ExistingOverride { get; }

        public CircuitRoomState(string uniqueId, string baseRoom, string existingOverride)
        {
            UniqueId = uniqueId ?? string.Empty;
            BaseRoom = baseRoom ?? string.Empty;
            ExistingOverride = existingOverride ?? string.Empty;
        }

        /// <summary>
        /// What this circuit's room currently resolves to: the override when set,
        /// otherwise the base room. This is the value the dialog prefills.
        /// </summary>
        public string EffectiveRoom =>
            string.IsNullOrWhiteSpace(ExistingOverride) ? BaseRoom : ExistingOverride;
    }

    /// <summary>
    /// The persisted result of the room-override field: the per-circuit changes to write
    /// (a blank value clears that circuit's override, falling back to geometry) and whether
    /// anything actually needs persisting. <see cref="ShouldPersist"/> is false whenever the
    /// user left the field untouched, so callers never open an empty transaction.
    /// </summary>
    public sealed class RoomOverrideDecision
    {
        public bool ShouldPersist { get; }
        public IReadOnlyDictionary<string, string> Changes { get; }

        public RoomOverrideDecision(bool shouldPersist, IReadOnlyDictionary<string, string> changes)
        {
            ShouldPersist = shouldPersist;
            Changes = changes;
        }

        public static readonly RoomOverrideDecision NoOp =
            new RoomOverrideDecision(false, new Dictionary<string, string>());
    }

    /// <summary>
    /// Pure decision logic behind the circuit-info dialog's Room Override field, shared by
    /// every command that creates or edits circuits (TurboWire, TurboDriver, …). Splitting
    /// this out of the Revit command keeps the fiddly "did the user change it → write /
    /// clear which circuits" rule under oracle tests instead of re-derived per command.
    ///
    /// The two rules that are easy to get wrong:
    /// <list type="bullet">
    /// <item><description>An <b>untouched</b> field is a true no-op — it must not stamp a
    /// batch's prefilled room onto circuits whose base room differs, nor clear an override
    /// the user left alone.</description></item>
    /// <item><description>Entered text that equals a circuit's own base room <b>clears</b>
    /// that circuit's override (falls back to geometry) rather than storing a redundant
    /// override.</description></item>
    /// </list>
    /// </summary>
    public static class CircuitRoomOverride
    {
        /// <summary>
        /// Shown (and left untouched as a no-op) when a batch of circuits resolves to more
        /// than one distinct room. Plain ASCII so an accidental edit is trivial to retype.
        /// </summary>
        public const string VariesPlaceholder = "<varies>";

        /// <summary>
        /// The text to prefill the Room Override field with: the circuits' shared effective
        /// room when they all agree, else <see cref="VariesPlaceholder"/>. Empty for an
        /// empty batch.
        /// </summary>
        public static string ComputePrefill(IReadOnlyList<CircuitRoomState> circuits)
        {
            if (circuits == null || circuits.Count == 0)
                return string.Empty;

            var distinct = circuits
                .Select(c => (c.EffectiveRoom ?? string.Empty).Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return distinct.Count == 1 ? distinct[0] : VariesPlaceholder;
        }

        /// <summary>
        /// Given the circuits, the prefill that was actually shown, and the text the user
        /// left in the field, decide what to persist. When the field is unchanged from the
        /// prefill the result is <see cref="RoomOverrideDecision.NoOp"/>. When changed, every
        /// circuit gets an entry: the entered text as an override, unless it equals that
        /// circuit's base room (then blank = clear). Persisting is skipped only if there is
        /// genuinely nothing to write and nothing to clear.
        /// </summary>
        public static RoomOverrideDecision Decide(
            IReadOnlyList<CircuitRoomState> circuits, string prefillShown, string enteredText)
        {
            if (circuits == null || circuits.Count == 0)
                return RoomOverrideDecision.NoOp;

            string entered = (enteredText ?? string.Empty).Trim();
            string prefill = (prefillShown ?? string.Empty).Trim();

            if (string.Equals(entered, prefill, StringComparison.OrdinalIgnoreCase))
                return RoomOverrideDecision.NoOp;

            var changes = new Dictionary<string, string>();
            bool anyOverride = false;
            foreach (var circuit in circuits)
            {
                string baseRoom = (circuit.BaseRoom ?? string.Empty).Trim();
                bool isOverride = entered.Length > 0
                    && !string.Equals(entered, baseRoom, StringComparison.OrdinalIgnoreCase);
                changes[circuit.UniqueId] = isOverride ? entered : string.Empty;
                anyOverride |= isOverride;
            }

            // Persist when there's a new override to write, or an existing override that
            // now needs clearing. Otherwise the "change" resolved to nothing storable.
            bool anyExisting = circuits.Any(c => !string.IsNullOrWhiteSpace(c.ExistingOverride));
            bool shouldPersist = anyOverride || anyExisting;

            return new RoomOverrideDecision(shouldPersist, changes);
        }
    }
}
