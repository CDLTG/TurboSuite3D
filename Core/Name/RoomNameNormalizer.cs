namespace TurboSuite.Name;

/// <summary>
/// The single room-name normalization rule used across TurboSuite: <b>trim → strip '#' → uppercase</b>.
///
/// Extracted from TurboName's CAD room-name extraction (<c>CadRoomExtractorService</c>, block + text modes)
/// so the space-naming command — which pulls an architect Room name onto a Space — produces byte-identical
/// names. Architects here author room names in lower case; this is where they become the firm's UPPER form.
/// Keep this the ONE place the rule lives so the two producers can never drift.
///
/// Deliberately does NOT collapse internal whitespace (TurboName never did), so <c>"A  B"</c> stays two
/// spaces. Culture-<c>ToUpper()</c> matches the shipped TurboName behavior (not invariant).
/// </summary>
public static class RoomNameNormalizer
{
    public static string Normalize(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        return raw.Trim().Replace("#", "").ToUpper();
    }
}
