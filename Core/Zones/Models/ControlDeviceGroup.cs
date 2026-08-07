#nullable enable
using System.Collections.Generic;

namespace TurboSuite.Zones.Models
{
    /// <summary>
    /// A class of modelled control device, counted the two ways that are needed and can no longer be
    /// the same number.
    ///
    /// <b>Devices are not order lines.</b> One keypad might order a base unit, two button kits and a
    /// faceplate — four lines, still one device on the link. One two-gang keypad is two devices on the
    /// link and still one base unit. Summing the order quantities to get a device count was correct
    /// only while every type declared exactly one part, and would silently over-size Clear Connect
    /// links the first time a repeater type declared a mounting bracket.
    ///
    /// Both come from one walk of the model, so they cannot disagree about the job.
    /// </summary>
    public sealed class ControlDeviceGroup
    {
        /// <summary>Devices on the control link. Instances, not parts.</summary>
        public int DeviceCount { get; set; }

        /// <summary>What to order, one row per catalog number.</summary>
        public IReadOnlyList<ControlDeviceTally> Tallies { get; set; } = new List<ControlDeviceTally>();
    }
}
