#nullable disable
using System;
using TurboSuite.Abstractions;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Number.ViewModels
{
    public class NumberableRowViewModel : ViewModelBase
    {
        private string _value;
        private bool _isDuplicate;

        public ElementRef ElementId { get; }
        public string DisplayLabel { get; }
        public string TypeName { get; }
        public string RoomName { get; }
        public string RoomNumber { get; }
        public string CircuitNumber { get; }
        public ElementRef CircuitElementId { get; }
        public string LoadName { get; }
        public string Panel { get; }
        public string Mark { get; }
        public int SlotNumber { get; set; }
        public int SlotRow { get; set; }
        public int SlotCol { get; set; }
        public string SlotType { get; set; }

        /// <summary>
        /// Model-space Y of the device. Drives a/b/c ordering of co-circuit power
        /// supplies so the suffix tracks live plan position, not list/Mark order.
        /// </summary>
        public double PositionY { get; }

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

        public NumberableRowViewModel(ElementRef elementId, string displayLabel, string value,
            string roomName = "", string roomNumber = "", string circuitNumber = "",
            ElementRef circuitElementId = default, string panel = "",
            string typeName = "", string loadName = "", string mark = "",
            double positionY = 0.0)
        {
            ElementId = elementId;
            DisplayLabel = displayLabel;
            _value = value;
            RoomName = roomName ?? "";
            RoomNumber = roomNumber ?? "";
            CircuitNumber = circuitNumber ?? "";
            CircuitElementId = circuitElementId;
            LoadName = loadName ?? "";
            Panel = panel ?? "";
            TypeName = typeName ?? "";
            Mark = mark ?? "";
            PositionY = positionY;
        }
    }
}
