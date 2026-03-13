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

    public static CadRoomSourceSettings CreateDefaults() => new()
    {
        Mode = "Block",
        BlockName = "",
        RoomNameTags = new List<string>(),
        CeilingHeightTag = "",
        RoomNameLayer = "",
        CeilingHeightLayer = ""
    };
}
