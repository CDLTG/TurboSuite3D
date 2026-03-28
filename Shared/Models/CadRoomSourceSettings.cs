using System.Collections.Generic;

namespace TurboSuite.Shared.Models;

public class CadRoomSourceSettings
{
    /// <summary>
    /// "Block" or "Text" — determines which fields are used.
    /// </summary>
    public string Mode { get; set; } = "Block";

    // ── Block mode ──

    /// <summary>Block name containing room attributes (e.g., "CDA_ROOM").</summary>
    public string BlockName { get; set; } = "";

    /// <summary>
    /// Ordered attribute tags whose values are concatenated (space-separated) to form the room name.
    /// E.g., ["003", "002"] → "NORTH" + "HALLWAY" = "NORTH HALLWAY".
    /// </summary>
    public List<string> RoomNameTags { get; set; } = new();

    /// <summary>Attribute tag containing ceiling height (e.g., "001").</summary>
    public string CeilingHeightTag { get; set; } = "";

    // ── Text mode ──

    /// <summary>CAD layer containing room name text (e.g., "ANNO_ROOM").</summary>
    public string RoomNameLayer { get; set; } = "";

    /// <summary>CAD layer containing ceiling height text. May be the same as RoomNameLayer.</summary>
    public string CeilingHeightLayer { get; set; } = "";

    // ── Text mode: ceiling height from blocks (optional override) ──

    /// <summary>Block name containing ceiling height attributes (used in Text mode when heights come from blocks).</summary>
    public string CeilingHeightBlockName { get; set; } = "";

    /// <summary>Attribute tag within the ceiling height block that holds the height value.</summary>
    public string CeilingHeightBlockTag { get; set; } = "";

    // ── Region generation ──

    /// <summary>CAD layer names containing wall lines (comma-separated in UI).</summary>
    public List<string> WallLayerNames { get; set; } = new();

    /// <summary>CAD layer names containing door geometry (comma-separated in UI).</summary>
    public List<string> DoorLayerNames { get; set; } = new();

    /// <summary>CAD layer names containing window geometry (comma-separated in UI).</summary>
    public List<string> WindowLayerNames { get; set; } = new();

    /// <summary>FilledRegionType name used for generated regions.</summary>
    public string RegionTypeName { get; set; } = "Room Region";

    public static CadRoomSourceSettings CreateDefaults() => new()
    {
        Mode = "Block",
        BlockName = "",
        RoomNameTags = new List<string>(),
        CeilingHeightTag = "",
        RoomNameLayer = "",
        CeilingHeightLayer = "",
        CeilingHeightBlockName = "",
        CeilingHeightBlockTag = "",
        WallLayerNames = new List<string>(),
        DoorLayerNames = new List<string>(),
        WindowLayerNames = new List<string>(),
        RegionTypeName = "Room Region"
    };
}
