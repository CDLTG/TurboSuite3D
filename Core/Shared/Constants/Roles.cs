using System;
using System.Collections.Generic;

namespace TurboSuite.Shared.Constants;

/// <summary>
/// The canonical <see cref="ParameterNames.TurboSuiteRole"/> vocabulary — the strings a family
/// author writes into the "TurboSuite Role" type parameter, and the constants call sites compare
/// against. Two jobs, one param:
///
/// <list type="bullet">
/// <item><b>Classify</b> — what a placed family <i>is</i> (Sconce, Keypad, …), driving special
/// behavior. Most families carry <i>no</i> role and take the default path.</item>
/// <item><b>Find</b> — which single family in a category to stamp (FixtureTypeTag, …); the finder
/// picks the <c>FamilySymbol</c> whose role matches, not the one whose name equals a string.</item>
/// </list>
///
/// The one pure seam is <see cref="Canonicalize"/>: it turns whatever an author typed (any case,
/// stray whitespace) into an exact constant, or <see cref="string.Empty"/> when blank, absent, or
/// unrecognized — so blank and "not a real role" collapse to the same "no special behavior". There
/// is deliberately <b>no</b> name-match fallback anywhere; this is the whole classification contract.
/// </summary>
public static class Roles
{
    // ── Classify roles (on placed model families) ────────────────────────────────────────────────
    public const string Sconce = "Sconce";
    public const string Chandelier = "Chandelier";
    public const string PictureLight = "PictureLight";
    public const string WallVertical = "WallVertical";
    public const string CeilingFan = "CeilingFan";
    public const string ExhaustFan = "ExhaustFan";
    public const string FireplaceIgniter = "FireplaceIgniter";
    public const string Receptacle = "Receptacle";
    public const string Switch = "Switch";
    public const string Keypad = "Keypad";
    public const string DriverPlaceholder = "DriverPlaceholder";
    public const string HybridRepeater = "HybridRepeater";

    // ── Finder roles (on tag / detail / annotation families) ─────────────────────────────────────
    public const string FixtureTypeTag = "FixtureTypeTag";
    public const string LinearTag = "LinearTag";
    public const string RunLengthTag = "RunLengthTag";
    public const string SwitchIdTag = "SwitchIdTag";
    public const string KeypadTag = "KeypadTag";
    public const string DeviceTypeTag = "DeviceTypeTag";
    public const string FixtureSwitchlegTag = "FixtureSwitchlegTag";
    public const string RemoteSwitchlegTag = "RemoteSwitchlegTag";
    public const string ElectricalSwitchlegTag = "ElectricalSwitchlegTag";
    public const string DeviceSwitchlegTag = "DeviceSwitchlegTag";
    public const string LinearFeedTag = "LinearFeedTag";
    public const string LinearFeedDetail = "LinearFeedDetail";
    public const string DmxDecoderDetail = "DmxDecoderDetail";
    public const string DmxDriverDetail = "DmxDriverDetail";
    public const string DmxInterfaceDetail = "DmxInterfaceDetail";
    public const string DmxProcessorDetail = "DmxProcessorDetail";
    public const string DmxTerminatorDetail = "DmxTerminatorDetail";
    public const string DmxWireMarkAnnotation = "DmxWireMarkAnnotation";

    /// <summary>Every recognized role, keyed case-insensitively to its canonical spelling.</summary>
    private static readonly IReadOnlyDictionary<string, string> Canonical =
        BuildCanonicalMap(new[]
        {
            Sconce, Chandelier, PictureLight, WallVertical, CeilingFan, ExhaustFan, FireplaceIgniter,
            Receptacle, Switch, Keypad, DriverPlaceholder, HybridRepeater,
            FixtureTypeTag, LinearTag, RunLengthTag, SwitchIdTag, KeypadTag, DeviceTypeTag,
            FixtureSwitchlegTag, RemoteSwitchlegTag, ElectricalSwitchlegTag, DeviceSwitchlegTag,
            LinearFeedTag, LinearFeedDetail, DmxDecoderDetail, DmxDriverDetail, DmxInterfaceDetail,
            DmxProcessorDetail, DmxTerminatorDetail, DmxWireMarkAnnotation,
        });

    /// <summary>
    /// Normalize a raw "TurboSuite Role" value to its exact constant. Trims and matches
    /// case-insensitively, so authoring is forgiving; returns <see cref="string.Empty"/> for null,
    /// blank, or any value outside the vocabulary. The single point where an authored string becomes
    /// a role — every classify/find site reads through here, so there is exactly one thing to test.
    /// </summary>
    public static string Canonicalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        Canonical.TryGetValue(raw.Trim(), out string? canon);
        return canon ?? string.Empty;
    }

    private static IReadOnlyDictionary<string, string> BuildCanonicalMap(string[] roles)
    {
        var map = new Dictionary<string, string>(roles.Length, StringComparer.OrdinalIgnoreCase);
        foreach (string role in roles)
            map[role] = role;
        return map;
    }
}
