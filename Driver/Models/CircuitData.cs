#nullable disable
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace TurboSuite.Driver.Models
{
    /// <summary>
    /// Data model representing an electrical circuit
    /// </summary>
    public class CircuitData
    {
        public ElementId CircuitId { get; set; }
        public string CircuitNumber { get; set; }
        public string LoadName { get; set; }
        public string LoadClassificationAbbreviation { get; set; }
        public int NumberOfElements { get; set; }
        public double ApparentPower { get; set; }
        public string Panel { get; set; }

        public List<FixtureData> LightingFixtures { get; set; }

        // Key = FamilyType name, Value = list of devices of that type
        public Dictionary<string, List<DeviceData>> DevicesByType { get; set; }

        public CircuitData()
        {
            LightingFixtures = new List<FixtureData>();
            DevicesByType = new Dictionary<string, List<DeviceData>>();
        }
    }
}
