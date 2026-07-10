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

    /// <summary>
    /// CAD layer names containing the overall building area/footprint boundary polyline
    /// (comma-separated in UI). Used as a hard exterior envelope during region generation —
    /// rooms cannot flood across it, eliminating exterior bleed.
    /// </summary>
    public List<string> AreaLayerNames { get; set; } = new();

    /// <summary>FilledRegionType name used for generated regions.</summary>
    public string RegionTypeName { get; set; } = "Room Region";

    /// <summary>
    /// File name (e.g. "x_Floor Plan.dwg") of the linked DWG the extractor should read. Empty = read every
    /// linked CAD in the view (legacy behavior). Scopes extraction to the floor plan so a co-linked RCP
    /// (which can share layer names) doesn't contribute stray geometry. Matched case-insensitively by file name.
    /// </summary>
    public string SourceLinkName { get; set; } = "";

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
        AreaLayerNames = new List<string>(),
        RegionTypeName = "Room Region",
        SourceLinkName = ""
    };
}
