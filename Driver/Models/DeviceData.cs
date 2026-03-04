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
    }
}
