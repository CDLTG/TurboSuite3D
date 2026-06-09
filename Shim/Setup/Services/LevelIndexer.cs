using System.Collections.Generic;

namespace TurboSuite.Setup.Services;

/// <summary>
/// Pure indexing logic for TurboSetup. Given a set of selected levels (already sorted by
/// elevation, ascending) and which one is "Main", produces the per-level index string used
/// in generated view names. No Revit references — the only unit-testable piece of TurboSetup.
///
/// Rule: Main = "01". Each step up in elevation among selected levels: "02", "03", ...
/// Each step down from Main: "00", "-01", "-02", ... Unselected levels contribute nothing
/// (no gaps), so a second basement reads "-01" naturally.
/// </summary>
internal static class LevelIndexer
{
    /// <summary>
    /// Returns index strings for positions 0..levelCount-1 (elevation order), where
    /// <paramref name="mainIndex"/> is the position of the Main level.
    /// </summary>
    public static IReadOnlyList<string> ComputeIndexStrings(int levelCount, int mainIndex)
    {
        var result = new string[levelCount];
        for (int i = 0; i < levelCount; i++)
        {
            // Position relative to Main: Main = 0, one up = +1, one down = -1.
            int p = i - mainIndex;
            result[i] = FormatIndex(p + 1);
        }
        return result;
    }

    /// <summary>
    /// Formats an index number: non-negative as two-digit zero-padded ("00", "01", ... "10"),
    /// negative as "-01", "-02", ... (two-digit magnitude).
    /// </summary>
    public static string FormatIndex(int n)
    {
        return n >= 0 ? n.ToString("D2") : "-" + (-n).ToString("D2");
    }
}
