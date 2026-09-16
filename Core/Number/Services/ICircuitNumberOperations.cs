using System.Collections.Generic;
using TurboSuite.Abstractions;
using TurboSuite.Number.Models;

namespace TurboSuite.Number.Services
{
    /// <summary>
    /// Revit-free contract for the CircuitNumber tab's panel-schedule + circuit
    /// operations. Implemented shim-side (wraps <c>PanelScheduleService</c>,
    /// <c>NumberWriterService</c>, <c>NumberCollectorService</c>); every method must run
    /// on the Revit API thread, so the Core VM invokes them inside
    /// <see cref="IRevitWorkQueue"/> work items.
    ///
    /// The panel-schedule view is passed back and forth as an opaque <see cref="object"/>
    /// handle — the VM only stores it and hands it to subsequent ops, never calling into
    /// it — so no Revit type leaks into Core. Grouped into one interface (vs many tiny
    /// ones) because these ops form a single cohesive surface over that one handle.
    /// </summary>
    public interface ICircuitNumberOperations
    {
        /// <summary>Returns the (existing or newly created) panel-schedule view as an
        /// opaque handle, or null if the panel can't be resolved.</summary>
        object GetOrCreateScheduleView(ElementRef panelRef);

        /// <summary>Reads the slot layout and resolves each slot's circuit number + load
        /// name into Revit-free <see cref="CircuitSlotData"/> (slots whose element is not
        /// an electrical circuit are omitted, matching the original VM projection).</summary>
        IReadOnlyList<CircuitSlotData> GetSlotLayout(object scheduleView);

        bool MoveCircuit(object scheduleView, int fromRow, int fromCol, int toRow, int toCol);

        /// <summary>Reorders the panel's circuits into <paramref name="roomOrder"/> —
        /// compacting real circuits contiguous at the top (stable within a room), clearing
        /// any Spare/Space placeholders to Empty, and sinking empties below — behind a
        /// preview/confirm dialog. Returns true only when the user confirmed and the sort
        /// was applied; false on "already sorted", cancel, or a refused swap (which rolls
        /// back so nothing changes).</summary>
        bool SortPanelByRoomOrder(object scheduleView, IReadOnlyList<string> roomOrder);
        bool AssignSpare(object scheduleView, IReadOnlyList<(int Row, int Col)> slots);
        bool AssignSpace(object scheduleView, IReadOnlyList<(int Row, int Col)> slots);
        bool RemoveSpareSpace(object scheduleView, IReadOnlyList<(int Row, int Col, string SlotType)> slots);

        void OpenScheduleView(object scheduleView);
        void WritePanelSettings(IReadOnlyList<PanelSettingsModel> panelSettings);
        IReadOnlyList<CircuitNumberRow> RefreshCircuits();
    }
}
