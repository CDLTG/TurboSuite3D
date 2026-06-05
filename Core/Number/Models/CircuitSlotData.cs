using TurboSuite.Abstractions;

namespace TurboSuite.Number.Models
{
    /// <summary>
    /// Revit-free projection of one panel-schedule slot, produced shim-side by
    /// <c>ICircuitNumberOperations.GetSlotLayout</c> (which resolves the circuit element,
    /// circuit number, and load name on the Revit API thread) and consumed by the Core
    /// CircuitNumber tab. Replaces the Revit-typed <c>SlotInfo</c> (which carries an
    /// <c>ElementId</c>) at the VM boundary. Empty/Spare/Space slots carry
    /// <see cref="CircuitRef"/> = <see cref="ElementRef.None"/>.
    /// </summary>
    public class CircuitSlotData
    {
        public ElementRef CircuitRef { get; set; }
        public string CircuitNumber { get; set; }
        public string LoadName { get; set; }
        public int SlotNumber { get; set; }
        public int SlotRow { get; set; }
        public int SlotCol { get; set; }
        public string SlotType { get; set; } // "Circuit", "Empty", "Spare", "Space"
    }
}
