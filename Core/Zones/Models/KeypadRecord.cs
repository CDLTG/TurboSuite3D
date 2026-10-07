#nullable enable
namespace TurboSuite.Zones.Models
{
    /// <summary>
    /// One physical wired keypad, as the control one-line's located list draws it (Phase C). A
    /// two-gang keypad is <b>one</b> record (its gang is a Type attribute — it doubles the packer's
    /// device <i>count</i>, never the list's row count). These per-keypad records are the source of
    /// truth the location-affinity pour assigns to links; the aggregate <see cref="KeypadCounts"/>
    /// numbers drive the capacity math. Wireless keypads carry no record — they stay the Clear
    /// Connect aggregate.
    /// </summary>
    public sealed class KeypadRecord
    {
        /// <summary>The keypad's "Switch ID" (TurboNumber's value). Empty when unnumbered — the row
        /// drops its id and sorts after numbered rows.</summary>
        public string SwitchId { get; }

        /// <summary>The resolved room name (3D Space or 2D Region), for the row label.</summary>
        public string Room { get; }

        /// <summary>The keypad's model for the row — the built-in <c>Model</c> parameter
        /// (<c>ALL_MODEL_MODEL</c>), the actual keypad catalog model, not the family type name.</summary>
        public string Model { get; }

        /// <summary>The keypad's location from the room→location map (0 = location-less: room not in
        /// the map, or no room resolved — these keep the plain job-wide pour).</summary>
        public int Location { get; }

        /// <summary>The keypad's QS-link device weight: <b>2</b> for a two-gang keypad, <b>1</b>
        /// otherwise. Drives the pour's capacity math only — the located list still draws one row per
        /// physical keypad (gang is a Type attribute, not a second row).</summary>
        public int Devices { get; }

        public KeypadRecord(string switchId, string room, string model, int location, int devices = 1)
        {
            SwitchId = switchId ?? string.Empty;
            Room = room ?? string.Empty;
            Model = model ?? string.Empty;
            Location = location;
            Devices = devices < 1 ? 1 : devices;
        }
    }
}
