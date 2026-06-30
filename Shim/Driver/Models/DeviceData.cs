#nullable disable
using Autodesk.Revit.DB;

namespace TurboSuite.Driver.Models
{
    /// <summary>
    /// Data model representing a lighting device
    /// </summary>
    public class DeviceData
    {
        public ElementId DeviceId { get; set; }
        public string SwitchID { get; set; }
        public ElementId CurrentFamilyTypeId { get; set; }
        public string CurrentFamilyTypeName { get; set; }

        /// <summary>DMX output count read off the device type (DMX Channels). &gt; 0 marks this device a
        /// DMX decoder rather than a wattage-sized driver — the signal TurboRPS uses to recognize a
        /// decoder-controlled circuit (TurboRPS-2).</summary>
        public int DmxChannels { get; set; }

        /// <summary>True when this device is a DMX decoder (<see cref="DmxChannels"/> &gt; 0).</summary>
        public bool IsDecoder => DmxChannels > 0;
    }
}
