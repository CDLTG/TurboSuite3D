#nullable disable
using Autodesk.Revit.DB;

namespace TurboSuite.Driver.Models
{
    /// <summary>
    /// Data model representing a lighting fixture
    /// </summary>
    public class FixtureData
    {
        public ElementId FixtureId { get; set; }
        public string TypeMark { get; set; }
        public string Comments { get; set; }
        public double LinearLength { get; set; }
        public double LinearPower { get; set; }
        public double TypePower { get; set; }
        public double EffectiveWattage => LinearPower > 0 ? LinearPower : TypePower;
        public bool IsLinear => LinearLength > 0;
        public string Manufacturer { get; set; }
        public string DimmingProtocol { get; set; }
        public string Voltage { get; set; }
    }
}
