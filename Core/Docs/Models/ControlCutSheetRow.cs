namespace TurboSuite.Docs.Models;

/// <summary>
/// One row of the Control Cut Sheets grid / output — a single cutsheet to bind, produced by
/// <c>ControlCutSheetRowBuilder</c> and mapped 1:1 onto a <c>FixtureSpecModel</c> by the shim.
/// Pure (Revit-free) so the resolve / URL-collapse / order / key logic is unit-testable.
/// </summary>
public sealed class ControlCutSheetRow
{
    /// <summary>Display label + PDF bookmark: combined part numbers for a shared closed-set cutsheet
    /// (e.g. "PD2-16F-120 / PD4-36F-120 / …"), or the <c>Model</c> name for a keypad/repeater.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Free-text description (the part's BOM description, or "Keypad"/"Repeater").</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Resolved cutsheet URL, or empty for a skipped ("no cutsheet") keypad/repeater row.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Stable persistence key (settings + local-PDF override): the normalized URL when the
    /// row resolved, else <c>model:&lt;Model&gt;</c> for a skipped row — never an empty string, so
    /// skipped rows don't collide.</summary>
    public string Key { get; set; } = string.Empty;
}
