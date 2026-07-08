#nullable enable

namespace TurboSuite.Dmx
{
    /// <summary>
    /// The DMX family-authoring conventions the model reader (Shim/Dmx/DmxModelReader) keys on to find DMX
    /// fixtures, decoders, and drivers — Revit parameter names + categories. CONFIRMED against the firm's
    /// real families 2026-06-25 (verified live: 95 fixtures / 5 zones read correctly). Centralized here
    /// (one place to change) rather than scattered as string literals; the reader stays defensive (an
    /// element missing a required param is skipped, never a crash).
    ///
    /// NET-NEW shared params for the module (ship in the project template): <see cref="ControlZone"/>
    /// (instance), <see cref="DmxChannels"/> (shared fixture+decoder), <see cref="DecoderAmpsPerChannel"/>,
    /// and <see cref="BundleSize"/> (fixture type — max fixtures per field-connectable chain).
    /// Everything else reuses existing TurboSuite conventions (Linear Length / Linear Power / Power /
    /// Voltage / Derating Factor).
    /// </summary>
    public static class DmxParameterNames
    {
        // ── DMX fixture (OST_LightingFixtures) — ANY channelized fixture, not just linear tape ────────
        // A lighting fixture is a DMX fixture iff Channels > 0 — this covers linear tape/sheet/landscape
        // AND point downlights alike (confirmed 2026-06-25).

        /// <summary>The one designer-set instance param grouping fixtures into control zones (native
        /// Properties; template shared param). A fixture with no value is "unassigned".</summary>
        public const string ControlZone = "Control Zone";

        /// <summary>Integer DMX channel count (1 single … 6 RGBATW) — the ONE shared param used on both
        /// fixtures and decoders (confirmed 2026-06-25): on a fixture it's the channels consumed and > 0
        /// marks it a DMX fixture; on a decoder it's the output count (see decoder section below).</summary>
        public const string DmxChannels = "DMX Channels";

        /// <summary>Run length in feet — existing <c>Linear Length</c>. <b>0 for point fixtures</b>
        /// (downlights/sheets); the reader then carries watts as a unit-length run.</summary>
        public const string LinearLength = "Linear Length";

        /// <summary>Total connected watts of a linear instance — existing <c>Linear Power</c>
        /// (= Linear Length × Power Per Length). Read first; <see cref="Power"/> is the point-fixture fallback.</summary>
        public const string LinearPower = "Linear Power";

        /// <summary>Total watts fallback for point fixtures where <c>Linear Power</c> is 0.</summary>
        public const string Power = "Power";

        /// <summary>Type param (net-new, DMX-only): the max number of these fixtures that can be
        /// field-connected in ONE daisy-chain/power tap — the atomic packable unit ("bundle"). A
        /// <b>max, not a divisor</b> (72 sheets @ 5 ⇒ 14 full chains + 1 remainder of 2 = 15 bundles).
        /// Prefixed "DMX " so non-DMX families never surface it. Missing/≤1 ⇒ no bundling (each fixture
        /// packs independently — today's behavior). Read live off the type each solve; no ES schema.</summary>
        public const string BundleSize = "DMX Bundle Size";

        // ── Decoder family (OST_LightingDevices) — caps read off the type (confirmed 2026-06-25) ──────
        // The decoder output count reuses the SAME shared DmxChannels param as fixtures (designers call
        // outputs "Channels" too). Presence of DmxChannels > 0 on a LightingDevice marks it a decoder
        // (categories distinguish it from a fixture). The total-watt cap reuses the shared Power param.

        /// <summary>C1 — max current on any one channel/output terminal.</summary>
        public const string DecoderAmpsPerChannel = "Amps Per Channel";

        // C2 — max total output watts: the shared Power param (see Power above).

        // ── Driver family (OST_LightingDevices) — specs read off the type ────────────────────────────
        // Reuses existing driver conventions. A device family that is NOT a decoder but has Voltage is a
        // driver; its rated watts is the shared Power param.
        public const string DriverVoltage = "Voltage";
        public const string DriverDeratingFactor = "Derating Factor";
    }
}
