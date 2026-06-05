#nullable disable
using TurboSuite.Abstractions;

namespace TurboSuite.Number.Models
{
    public class CircuitNumberRow
    {
        public ElementRef ElementId { get; set; }
        public string CircuitNumber { get; set; }
        public string Panel { get; set; }
        public string LoadName { get; set; }
    }
}
