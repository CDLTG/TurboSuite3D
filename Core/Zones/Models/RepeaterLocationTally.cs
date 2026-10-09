#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Zones.Models
{
    /// <summary>
    /// One repeater location — the location a job's hybrid repeaters (and the wireless keypads
    /// circuited to them) sit in — and how the Clear Connect link math pools there. The wireless
    /// analog of <see cref="ShadeLocationTally"/>: the shim groups repeaters by their panel-name
    /// location (<c>{Location}-REP{N}</c>) and the wireless keypads by the Controls circuit's panel
    /// name, and the packer sizes CC-A links <b>per location</b> — <c>Σ_loc ceil(repeaters_loc / 4)</c>
    /// with the 99-device cap as the backstop — exactly as located shade/dimmer panels pool.
    ///
    /// <para>Mirrors shades' "located ⇒ per-location, unlocated ⇒ warning" split: a tally whose
    /// <see cref="LocationName"/> parses to a real location number is sized and placed; one that does
    /// not is a warning, never a link (see <c>ControlLinkPacker</c>).</para>
    ///
    /// <para><see cref="WirelessKeypads"/> carries the location's per-keypad fan records in Switch-ID
    /// order, so the one-line can fan them under their repeater (F4). It is additive — the CC-A sizing
    /// reads <see cref="RepeaterCount"/> and the keypad <see cref="WirelessDeviceCount"/>, not the
    /// records — so an empty list just means no fan is drawn.</para>
    /// </summary>
    public sealed class RepeaterLocationTally
    {
        public RepeaterLocationTally(string locationName, int location,
            IReadOnlyList<RepeaterRecord>? repeaters = null)
        {
            LocationName = locationName ?? string.Empty;
            Location = location;
            Repeaters = repeaters ?? Array.Empty<RepeaterRecord>();
        }

        /// <summary>A representative panel name for this location (e.g. "1-REP1"), for diagnostics and
        /// F4's stamp labels. "(unassigned)" when no repeater here carries a parseable one.</summary>
        public string LocationName { get; }

        /// <summary>
        /// The parsed location number this pool sizes under — the grouping key, not a re-parse of
        /// <see cref="LocationName"/>, so the orphan→host relabel can <b>rewrite</b> it (an orphaned
        /// location's repeaters fold into their host's Clear Connect pool) while the display name
        /// stays. 0 = unlocated (a warning upstream, dropped from the located sizing path).
        /// </summary>
        public int Location { get; }

        /// <summary>The repeaters at this location, each with its own circuited keypads (F4) — the grain
        /// the one-line's per-repeater fan draws. Ordered by panel name for a stable stamp chain.</summary>
        public IReadOnlyList<RepeaterRecord> Repeaters { get; }

        /// <summary>Physical hybrid repeaters at this location — the <c>ceil(/4)</c> numerator.</summary>
        public int RepeaterCount => Repeaters.Count;

        /// <summary>The wireless keypads circuited to this location's repeaters, flattened across them in
        /// Switch-ID order. Empty ⇒ the fan falls back to the count/stub.</summary>
        public IReadOnlyList<KeypadRecord> WirelessKeypads => Repeaters.SelectMany(r => r.Keypads).ToList();

        /// <summary>Wireless keypad <b>devices</b> riding this location's links (a two-gang keypad is
        /// two), the second input to the 99-device cap backstop alongside the repeaters.</summary>
        public int WirelessDeviceCount => Repeaters.Sum(r => r.KeypadDeviceCount);
    }
}
