#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Zones.Models
{
    /// <summary>
    /// One hybrid repeater and the wireless keypads circuited to it (F4) — the per-repeater grain the
    /// control one-line's repeater fan draws. The provider reads each wireless keypad's <c>Controls</c>
    /// circuit, whose panel <b>is</b> the repeater, so it knows which keypads ride which repeater; this
    /// record carries that assignment through the packer to the stamp so each repeater fans its OWN
    /// keypads (not a flat per-link list).
    ///
    /// <para>The CC-A sizing is unchanged — it still counts repeaters (4/link) and devices (99/link); a
    /// record just keeps the keypads attached to their repeater so the one-line can draw them under the
    /// right stamp.</para>
    /// </summary>
    public sealed class RepeaterRecord
    {
        public RepeaterRecord(string panelName, int location, IReadOnlyList<KeypadRecord>? keypads = null)
        {
            PanelName = panelName ?? string.Empty;
            Location = location;
            Keypads = keypads ?? Array.Empty<KeypadRecord>();
        }

        /// <summary>The repeater's panel name (e.g. "1-REP1") — its identity and deterministic sort key.</summary>
        public string PanelName { get; }

        /// <summary>The parsed location this repeater sits in (0 = unlocated → a warning, dropped from the
        /// located sizing path).</summary>
        public int Location { get; }

        /// <summary>The wireless keypads circuited to this repeater, in Switch-ID order — the fan rows.</summary>
        public IReadOnlyList<KeypadRecord> Keypads { get; }

        /// <summary>Wireless keypad <b>devices</b> this repeater carries (a two-gang keypad is two).</summary>
        public int KeypadDeviceCount => Keypads.Sum(k => k.Devices);

        /// <summary>The repeater itself (1) plus its keypad devices — this repeater's draw on the 99-device
        /// Clear Connect cap.</summary>
        public int DeviceCount => 1 + KeypadDeviceCount;

        /// <summary>A copy relabelled onto another location (the orphan→host fold), keypads carried along.</summary>
        public RepeaterRecord WithLocation(int location) => new RepeaterRecord(PanelName, location, Keypads);
    }
}
