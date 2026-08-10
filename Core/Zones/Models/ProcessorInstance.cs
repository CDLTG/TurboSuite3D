#nullable disable
namespace TurboSuite.Zones.Models
{
    /// <summary>
    /// One HQP7-2 processor as the sidebar draws it — a label and its two link bars.
    ///
    /// <b>The display unit is the processor, not the panel.</b> An LV21 with "Processor" in <i>both</i>
    /// of its compartments is two processors, hence two of these and four link bars — which a single
    /// <see cref="PanelResult"/> (one <c>Link1</c>/<c>Link2</c> pair) cannot express. This is the same
    /// per-slot count the BOM's supply sizer uses, so the bars and the order agree: four links of
    /// capacity, two power supplies.
    /// </summary>
    public sealed class ProcessorInstance
    {
        /// <summary>The panel this processor is sited in — several instances can share it.</summary>
        public string PanelName { get; set; }

        /// <summary>Sidebar header text: the panel name, or "1-A (2)" when the panel holds more than one
        /// processor, so two instances of the same LV21 are told apart.</summary>
        public string Label { get; set; }

        public ProcessorLink Link1 { get; set; }
        public ProcessorLink Link2 { get; set; }
    }
}
