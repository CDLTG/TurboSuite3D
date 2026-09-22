using System.Collections.Generic;

namespace TurboSuite.Docs.Models;

/// <summary>
/// One room section of the By-Room Load Schedule: a header room name and the circuits filed under
/// it, already sorted for display. <see cref="IsNoRoom"/> marks the trailing catch-all section
/// (device-only circuits, coverage gaps) whose header prints no wattage summary.
/// </summary>
public class LoadSection
{
    public string RoomName { get; }
    public bool IsNoRoom { get; }
    public List<LoadsCircuitModel> Circuits { get; }

    public LoadSection(string roomName, bool isNoRoom, List<LoadsCircuitModel> circuits)
    {
        RoomName = roomName;
        IsNoRoom = isNoRoom;
        Circuits = circuits;
    }
}
