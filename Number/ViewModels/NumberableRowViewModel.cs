#nullable disable
using System;
using Autodesk.Revit.DB;

namespace TurboSuite.Number.ViewModels
{
    public class NumberableRowViewModel : ViewModelBase
    {
        private string _value;
        private bool _isDuplicate;

        public ElementId ElementId { get; }
        public string DisplayLabel { get; }
        public string TypeName { get; }
        public string RoomName { get; }
        public string RoomNumber { get; }
        public string CircuitNumber { get; }
        public string LoadName { get; }
        public string Panel { get; }
        public string Mark { get; }
        public int SlotNumber { get; set; }
        public int SlotRow { get; set; }
        public int SlotCol { get; set; }
        public string SlotType { get; set; }

        public bool IsDuplicate
        {
            get => _isDuplicate;
            set => SetProperty(ref _isDuplicate, value);
        }

        public string Value
        {
            get => _value;
            set
            {
                if (SetProperty(ref _value, value))
                    ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler ValueChanged;

        public NumberableRowViewModel(ElementId elementId, string displayLabel, string value,
            string roomName = "", string roomNumber = "", string circuitNumber = "", string panel = "",
            string typeName = "", string loadName = "", string mark = "")
        {
            ElementId = elementId;
            DisplayLabel = displayLabel;
            _value = value;
            RoomName = roomName ?? "";
            RoomNumber = roomNumber ?? "";
            CircuitNumber = circuitNumber ?? "";
            LoadName = loadName ?? "";
            Panel = panel ?? "";
            TypeName = typeName ?? "";
            Mark = mark ?? "";
        }
    }
}
