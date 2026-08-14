using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using TurboSuite.Circuits;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.Views;

namespace TurboSuite.Shared.Services;

/// <summary>What the circuit-info prompt did, so callers can decide whether to keep going.</summary>
public enum CircuitInfoResult
{
    /// <summary>The dialog was shown and its choices applied.</summary>
    Applied,

    /// <summary>Nothing to prompt for — setting off, or only switched circuits — treated as success.</summary>
    Skipped,

    /// <summary>The user cancelled the dialog; the caller should abort (and roll back).</summary>
    Cancelled
}

/// <summary>
/// The shared "circuit metadata" pipeline: resolve the comment / room / panel defaults for a
/// batch of freshly created-or-wired circuits, show <see cref="CircuitInfoDialog"/> (gated by
/// the General setting), and persist the user's choices. TurboWire and TurboDriver both call
/// <see cref="PromptAndApply"/> — Wire with the circuits it just wired, Driver with its single
/// circuit — so the whole comment/room/panel experience has one implementation.
/// </summary>
public static class CircuitInfoService
{
    /// <summary>
    /// Resolve defaults, prompt, and apply for the given circuits. Switched circuits are
    /// filtered out (they are unassigned "switched" by design and never take this dialog).
    /// Returns <see cref="CircuitInfoResult.Skipped"/> when the setting is off or nothing is
    /// left to prompt for, <see cref="CircuitInfoResult.Cancelled"/> if the user cancels, and
    /// <see cref="CircuitInfoResult.Applied"/> otherwise. Runs its own transactions; callers
    /// keep their own grouping (e.g. TurboWire's TransactionGroup) around it.
    /// </summary>
    public static CircuitInfoResult PromptAndApply(
        Document doc, IReadOnlyList<ElectricalSystem> circuits, string caption,
        bool shadePanels = false)
    {
        if (!GeneralSettingsCache.Get(doc).ShowCircuitCommentsDialog)
            return CircuitInfoResult.Skipped;

        var targets = (circuits ?? Array.Empty<ElectricalSystem>())
            .Where(c => c != null && !IsSwitched(c))
            .ToList();
        if (targets.Count == 0)
            return CircuitInfoResult.Skipped;

        var circuitNumbers = string.Join(", ", targets
            .Select(c => ParameterHelper.GetCircuitNumber(c))
            .Where(n => !string.IsNullOrEmpty(n)));

        var existingComments = CircuitService.GetExistingComments(doc);
        // Shade mode lists only shade (35 V) locations; lighting lists everything else. The
        // dropdown label ("Zone") and every other field are identical either way.
        var panels = shadePanels
            ? CircuitService.GetShadePanels(doc)
            : CircuitService.GetAllPanels(doc);
        // Default the panel dropdown to the last circuit's choice — a real panel, or <None>
        // when the previous circuit was deliberately left unassigned. Exclude the circuits
        // being handled now so they reflect the prior state, not themselves.
        var (autoPanel, preferNone) = CircuitService.FindLastPanelChoice(
            doc, targets.Select(c => c.Id).ToList(), shadePanels);

        // Resolve each circuit's live base room (owned Spaces, region fallback in 2D) the same
        // way TurboZones does — first lighting/electrical fixture on the circuit.
        var regionFallback = new RegionRoomLookupService(doc);
        var roomCache = new SpaceRoomFinderService.SpaceLookupCache(doc, regionFallback);
        var existingOverrides = RoomOverrideStorageService.Load(doc);

        var states = targets
            .Select(c => new CircuitRoomState(
                c.UniqueId,
                ResolveBaseRoom(c, roomCache),
                existingOverrides.TryGetValue(c.UniqueId, out var ov) ? ov : string.Empty))
            .ToList();

        string roomPrefill = CircuitRoomOverride.ComputePrefill(states);
        string commentPrefill = SharedComment(targets);
        var roomNames = CollectProjectRoomNames(doc, regionFallback);

        var dialog = new CircuitInfoDialog(existingComments, panels, autoPanel, circuitNumbers,
            roomPrefill, roomNames, preferNone)
        {
            Title = string.IsNullOrEmpty(caption) ? "Circuit Info" : $"{caption} — Circuit Info",
            CommentsPrefill = commentPrefill
        };

        if (dialog.ShowDialog() != true)
            return CircuitInfoResult.Cancelled;

        // Comment: write only when the user actually changed the field from the prefill. The
        // prefill is display-only — for a blank circuit it shows the fixture-derived fallback
        // (what TurboZones would resolve), so an untouched field must NOT stamp that onto the
        // circuit's Comments param: doing so would turn TurboZones' auto fixture-comment into a
        // sticky circuit-comment override that stops tracking the fixtures. A genuine edit
        // applies to every circuit in the batch. (Blank stays "keep existing", as before.)
        string enteredComment = dialog.CommentsText ?? string.Empty;
        bool commentChanged = !string.Equals(enteredComment.Trim(), commentPrefill.Trim(),
            StringComparison.Ordinal);
        if (commentChanged && !string.IsNullOrEmpty(enteredComment))
        {
            foreach (var circuit in targets)
                CircuitService.SetCircuitComments(doc, circuit, enteredComment);
        }

        // Room override: pure decision (untouched → no-op; typed text equal to a circuit's base
        // room clears it; else stored). See Core/Circuits/CircuitRoomOverride.
        var decision = CircuitRoomOverride.Decide(states, roomPrefill, dialog.RoomOverrideText);
        if (decision.ShouldPersist)
        {
            using var t = new Transaction(doc, "Circuit room override");
            t.Start();
            RoomOverrideStorageService.Upsert(doc,
                decision.Changes.ToDictionary(kv => kv.Key, kv => kv.Value));
            t.Commit();
        }

        if (dialog.UnassignPanel)
        {
            // User picked <None> — strip any auto-assigned panel (DMX/DALI etc.)
            foreach (var circuit in targets)
            {
                if (circuit.BaseEquipment != null)
                    CircuitService.ClearCircuitPanel(doc, circuit);
            }
        }
        else if (dialog.SelectedPanel != null)
        {
            // Re-assign panel if the user picked a different one.
            foreach (var circuit in targets)
            {
                if (circuit.BaseEquipment?.Id != dialog.SelectedPanel.Id)
                    CircuitService.SetCircuitPanel(doc, circuit, dialog.SelectedPanel);
            }
        }

        return CircuitInfoResult.Applied;
    }

    private static bool IsSwitched(ElectricalSystem circuit) =>
        string.Equals(ParameterHelper.GetCircuitComments(circuit), "switched",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The comment to prefill: the batch's shared <i>effective</i> comment when they all agree,
    /// else blank. Display-only — see the write-only-on-change guard in <see cref="PromptAndApply"/>.
    /// </summary>
    private static string SharedComment(IReadOnlyList<ElectricalSystem> circuits)
    {
        var distinct = circuits
            .Select(EffectiveComment)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return distinct.Count == 1 ? distinct[0] : string.Empty;
    }

    /// <summary>
    /// A circuit's effective comment: its own Comments param when set, otherwise the fixtures'
    /// comment the same way TurboZones resolves a load-name label from a comment-less circuit —
    /// the distinct, non-empty instance Comments of its lighting/electrical fixtures, joined with
    /// ", ". Returned raw (not parenthetical-stripped): this is the comment source, and TurboZones
    /// does its own stripping at load-name time.
    /// </summary>
    private static string EffectiveComment(ElectricalSystem circuit)
    {
        string circuitComment = ParameterHelper.GetCircuitComments(circuit);
        if (!string.IsNullOrWhiteSpace(circuitComment))
            return circuitComment;

        return string.Join(", ", CircuitService.GetFixturesOnCircuit(circuit)
            .Select(ParameterHelper.GetComments)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct());
    }

    /// <summary>
    /// Live base room for a circuit: the room resolved from its first lighting/electrical
    /// fixture (matches TurboZones' convention). Empty if the circuit has no such fixture or
    /// no room resolves. Devices (power supplies) are excluded by
    /// <see cref="CircuitService.GetFixturesOnCircuit"/>, so a driver placed outside the room
    /// never skews the result.
    /// </summary>
    private static string ResolveBaseRoom(ElectricalSystem circuit,
        SpaceRoomFinderService.SpaceLookupCache roomCache)
    {
        var fixtures = CircuitService.GetFixturesOnCircuit(circuit);
        if (fixtures.Count == 0)
            return string.Empty;
        return roomCache.FindRoomName(fixtures[0]) ?? string.Empty;
    }

    /// <summary>
    /// Distinct, sorted room names for the Room Override search/autofill: real Rooms across the
    /// host document and all linked models, plus "Room Region" names from the 2D fallback so
    /// drafting jobs (which have no Rooms) still get suggestions.
    /// </summary>
    private static List<string> CollectProjectRoomNames(Document doc,
        RegionRoomLookupService regionFallback)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Collect(Document d)
        {
            foreach (var room in new FilteredElementCollector(d)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .OfClass(typeof(SpatialElement))
                .Cast<Autodesk.Revit.DB.Architecture.Room>())
            {
                string? name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name!.Trim());
            }
        }

        Collect(doc);
        foreach (var link in new FilteredElementCollector(doc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>())
        {
            var linkDoc = link.GetLinkDocument();
            if (linkDoc != null)
                Collect(linkDoc);
        }

        // 2D drafting: "Room Region" names, so jobs with no Room elements still list.
        foreach (var name in regionFallback.RoomNames)
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name.Trim());

        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
