#nullable disable
using Autodesk.Revit.DB;

namespace TurboSuite.Number.Models
{
    public class DeviceNumberRow
    {
        public ElementId ElementId { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public string Model { get; set; }
        public string SwitchId { get; set; }
        public string RoomName { get; set; }
        public string RoomNumber { get; set; }
        public string CircuitNumber { get; set; }
        public ElementId CircuitElementId { get; set; }
        public string LoadName { get; set; }
        public string Mark { get; set; }

        /// <summary>
        /// Model-space Y of the device location. Used to order multiple power supplies
        /// on the same circuit top-to-bottom for a/b/c suffixing, matching the column
        /// TurboDriver stacks along (model −Y). View-independent by design — see
        /// PowerSupplyTabViewModel.AutoNumber.
        /// </summary>
        public double PositionY { get; set; }
    }
}
