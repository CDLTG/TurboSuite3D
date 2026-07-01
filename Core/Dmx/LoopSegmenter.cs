using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Dmx
{
    /// <summary>One repeater-bounded RS-485 run within a DMX loop (§6b / Hierarchy rung 5a).</summary>
    public sealed class SignalSegment
    {
        public SignalSegment(int deviceCount) { DeviceCount = deviceCount; }

        /// <summary>DMX devices on this segment (decoders; the bridge counts too — §6b rule 4).</summary>
        public int DeviceCount { get; }
    }

    /// <summary>How a loop's daisy chain was split into segments by repeaters/splitters.</summary>
    public sealed class LoopSegmentation
    {
        public LoopSegmentation(IReadOnlyList<SignalSegment> segments) { Segments = segments; }

        public IReadOnlyList<SignalSegment> Segments { get; }
        public int SegmentCount => Segments.Count;

        /// <summary>Repeaters/splitter-outputs needed: one fresh segment per split beyond the first.</summary>
        public int RepeaterCount => Math.Max(0, SegmentCount - 1);

        /// <summary>Repeaters regenerate the signal transparently — they cost ZERO DMX channels (§6b).</summary>
        public int ExtraChannelCost => 0;
    }

    /// <summary>
    /// Step 8 — split a loop's devices into signal segments under D4 (devices/segment). D4 is a
    /// CONTRACT INPUT (RS-485 default ~32, vendor-unspecified), not a constant. The fix follows the
    /// axis that broke: too many devices ⇒ repeater, keeping 1 interface / 1 address / 0 channels
    /// (§6b). Splitting is BALANCED (~27/26), not fill-and-spill, to leave each segment re-run
    /// headroom (§6b rule 2). D3 (length) is flag-only and needs geometry we don't model — not here.
    /// </summary>
    public static class LoopSegmenter
    {
        /// <summary>The RS-485 unit-load limit: max DMX devices on one repeater-bounded segment (§6b).
        /// The DMX512/RS-485 standard figure — a fixed hardware property of the bus, not a per-job knob,
        /// so it's a Core constant rather than a UI setting. A loop denser than this is split by repeaters
        /// (0 channels, 1 address). Change here (one-line) only if a gateway/decoder kit reliably drives more.</summary>
        public const int DevicesPerSegment = 32;

        public static LoopSegmentation Segment(int deviceCount, int maxDevicesPerSegment)
        {
            if (deviceCount < 0) throw new ArgumentOutOfRangeException(nameof(deviceCount));
            if (maxDevicesPerSegment <= 0) throw new ArgumentOutOfRangeException(nameof(maxDevicesPerSegment));
            if (deviceCount == 0) return new LoopSegmentation(Array.Empty<SignalSegment>());

            int segments = (int)Math.Ceiling((double)deviceCount / maxDevicesPerSegment);
            int baseSize = deviceCount / segments;
            int remainder = deviceCount % segments;

            // `remainder` segments carry one extra device; emit the larger ones first (27 before 26).
            var sizes = new List<SignalSegment>(segments);
            for (int i = 0; i < segments; i++)
                sizes.Add(new SignalSegment(i < remainder ? baseSize + 1 : baseSize));

            return new LoopSegmentation(sizes);
        }
    }
}
