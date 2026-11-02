#nullable disable
using Autodesk.Revit.DB;

namespace TurboSuite.Setup.Services;

/// <summary>
/// Turns on the link type's "Room Bounding" (the Edit Type checkbox) for the architectural link chosen
/// in Stage 1, so host Spaces placed in the generated Spaces views find their boundaries from the linked
/// walls/floors. Only that link — consultant links (MEP, etc.) shouldn't bound rooms; a structural link
/// that should is still a manual tick.
///
/// The checkbox is a TYPE parameter (<see cref="BuiltInParameter.WALL_ATTR_ROOM_BOUNDING"/>, Integer
/// Yes/No) on <see cref="RevitLinkType"/>; the instance doesn't carry it. Writable and persists through
/// commit — verified by TurboSpike probe (Revit 2025).
///
/// Caller owns the transaction.
/// </summary>
internal static class LinkRoomBoundingService
{
    internal enum Outcome
    {
        TurnedOn,
        AlreadyOn,

        /// <summary>The type has no writable Room Bounding parameter — left for a manual tick.</summary>
        NotSettable
    }

    public static Outcome Enable(RevitLinkType linkType)
    {
        Parameter p = linkType?.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
        if (p == null || p.IsReadOnly || p.StorageType != StorageType.Integer)
            return Outcome.NotSettable;
        if (p.AsInteger() == 1)
            return Outcome.AlreadyOn;
        return p.Set(1) ? Outcome.TurnedOn : Outcome.NotSettable;
    }
}
