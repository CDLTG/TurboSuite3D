#nullable disable
using TurboSuite.Abstractions;

namespace TurboSuite.Driver.Models
{
    /// <summary>
    /// Data model representing a lighting fixture
    /// </summary>
    public class FixtureData
    {
        public ElementRef FixtureId { get; set; }
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

        /// <summary>
        /// True when the fixture's "Remote Power Supply" type parameter is checked — i.e. it
        /// needs a driver. On a switched/relay circuit, line-voltage fixtures (e.g. recessed
        /// downlights) share the circuit for total-wattage/power-density purposes but must NOT
        /// feed the driver recommendation. TurboRPS sizes drivers over RPS fixtures only.
        /// </summary>
        public bool HasRemotePowerSupply { get; set; }
    }
}
