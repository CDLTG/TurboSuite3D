namespace TurboSuite.Shared.Services;

/// <summary>
/// Classifies an electrical-equipment panel as a <b>lighting</b> panel (one a lighting circuit can
/// live on) or a <b>shade/control</b> panel, purely from the name of the distribution system it
/// distributes downstream. Revit-free so it can be unit-pinned; the shim reads the panel's
/// distribution-system name (<c>RBS_FAMILY_CONTENT_DISTRIBUTION_SYSTEM</c>.AsValueString()) and
/// hands it here.
///
/// <para><b>Why the distribution system, not the name.</b> Shade panels always sit on the 35 V
/// distribution system that ships in the project template, whether or not their panel *name* is
/// filled in — verified in-model (TurboSpike): two blank-named shade panels both reported "35 V",
/// the lighting control panel reported "120 V". A light circuit physically cannot live on a 35 V
/// system, so this is the same compatibility signal Revit itself uses — not a naming convention.</para>
///
/// <para><b>The rule (per the designer): exactly "35 V" is shade/control; everything else is a
/// lighting panel.</b> Kept as an exact match rather than a voltage threshold so it stays a single,
/// legible anchor. <b>Fail open</b> for the lighting side: a null/empty/unreadable distribution
/// system is treated as a lighting panel, so the picker can never hide a legitimate panel.
/// <see cref="IsShadePanel"/> is the deliberate near-inverse: it answers true only for a
/// positively-identified shade system, so a shade-only picker never grabs an unknown panel.</para>
/// </summary>
public static class PanelClassifier
{
    /// <summary>The distribution-system name that marks a shade/control panel (template-provided).</summary>
    public const string ShadeDistributionSystem = "35 V";

    /// <summary>
    /// True when a lighting circuit may live on this panel. Fails open: a null/empty/unrecognized
    /// distribution system counts as a lighting panel so no real panel is ever hidden. Only the
    /// exact shade distribution system (<see cref="ShadeDistributionSystem"/>) is excluded.
    /// </summary>
    public static bool IsLightingPanel(string? distributionSystemName) =>
        !IsShadePanel(distributionSystemName);

    /// <summary>
    /// True only when this panel is positively identified as a shade/control panel — its
    /// distribution system is exactly <see cref="ShadeDistributionSystem"/> (case-insensitive,
    /// trimmed). A null/empty/unknown system is <b>not</b> a shade panel.
    /// </summary>
    public static bool IsShadePanel(string? distributionSystemName) =>
        !string.IsNullOrWhiteSpace(distributionSystemName) &&
        string.Equals(distributionSystemName!.Trim(), ShadeDistributionSystem,
            System.StringComparison.OrdinalIgnoreCase);
}
