using System;
using System.Collections.Generic;

namespace TurboSuite.Docs.Services;

/// <summary>
/// Hard-coded cutsheet-URL map for control parts, the source-of-truth for the TurboDocs Cut Sheets
/// "Control Package" mode. Two keys, because the control-part universe is half closed / half open:
///
/// <list type="bullet">
///   <item><b><see cref="ResolvePart"/> — keyed by part number.</b> The closed hardware set defined
///     in <c>BrandConfig</c> (+ the shade panel and DALI module). These are BOM <i>recommendations</i>,
///     never placed elements, so a part number is their only identity.</item>
///   <item><b><see cref="ResolveModel"/> — keyed by the <c>Model</c> parameter.</b> Keypads and hybrid
///     repeaters are <i>placed</i> families with thousands of color/button/engraving SKUs that all
///     share one cutsheet; <c>Model</c> is 1:1 with the cutsheet, so it — not the catalog number — is
///     the key.</item>
/// </list>
///
/// A part deliberately without a standalone cutsheet (the wire harnesses — "included with the panel
/// cutsheet") lives in <see cref="KnownNoCutsheet"/>, kept apart from the map so
/// <see cref="IsAccountedFor"/> can distinguish "no cutsheet by design" from "someone forgot a URL":
/// a completeness test asserts every part a brand config can emit is in one set or the other.
///
/// URLs are transcribed from <c>Specs/control-cutsheet-urls.txt</c> (the dev-authored fill-in list).
/// When a link rots, the runtime local-PDF override in the grid is the escape hatch — no code change.
/// </summary>
public static class ControlCutSheetUrlResolver
{
    private const string Lutron = "https://assets.lutron.com/a/documents/";

    // Part number → cutsheet URL. Case-insensitive. Several parts deliberately share a URL (all the
    // PD-series panels are on one panel cutsheet); the row builder collapses identical URLs to one page.
    private static readonly Dictionary<string, string> PartUrls =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Lutron — processor
            ["HQP7-2"] = Lutron + "3691127_eng.pdf",

            // Lutron — power panels (PD-series share one cutsheet)
            ["HQ-LV21-120"] = Lutron + "369-381.pdf",
            ["PD2-16F-120"] = Lutron + "3691055_eng.pdf",
            ["PD4-36F-120"] = Lutron + "3691055_eng.pdf",
            ["PD5-36F-120"] = Lutron + "3691055_eng.pdf",
            ["PD8-59F-120"] = Lutron + "3691055_eng.pdf",
            ["PD9-59F-120"] = Lutron + "3691055_eng.pdf",

            // Lutron — DIN power/dimming modules
            ["LQSE-4A5-120-D"] = Lutron + "3691126_eng.pdf",
            ["LQSE-4T5-120-D"] = Lutron + "3691054_eng.pdf",
            ["LQSE-4S8-120-D"] = Lutron + "3691060_eng.pdf",
            ["LQSE2-1DALUNV-D"] = Lutron + "3691342_eng.pdf", // DALI (experimental; dev/test builds)

            // Lutron — power supply
            ["QSPS-DH-1-75-H"] = Lutron + "369886_eng.pdf",

            // Lutron — interfaces
            ["QSE-IO"] = Lutron + "qse-io.pdf",
            ["QSE-CI-DMX"] = Lutron + "369372_qse-ci-dmx.pdf", // DMX (experimental; dev/test builds)

            // Lutron — shades
            ["QSPS-10PNL"] = Lutron + "085335.pdf",

            // Crestron — enclosure + modules
            ["CAEN-7X1"] = "https://www.crestron.com/getmedia/35609e0d-b113-48cb-b777-2e395ff0d4fe/ss_caen",
            ["CLX-2DIMU8"] = "https://www.crestron.com/getmedia/026a8255-d86d-4768-99b6-4cfb49153df0/ss_clx-2dimu8",
            ["CLX-2DIMFLV8"] = "https://www.crestron.com/getmedia/6fdf3c5e-3cda-49b0-9a71-59584d12fe46/ss_clx-2dimflv8",
            ["CLX-4HSW4"] = "https://www.crestron.com/getmedia/85f225c6-fe4b-4ac0-86b4-cc4dce0d7301/ss_clx-4hsw4",
        };

    // Placed keypad / hybrid-repeater Model → cutsheet URL. Case-insensitive, trimmed. Every SKU of a
    // family carries one of these Model values (1:1 with the cutsheet). Some are Crestron lines.
    private static readonly Dictionary<string, string> ModelUrls =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Alisse"] = Lutron + "3691154_eng.pdf",
            ["Cameo"] = "https://www.crestron.com/getmedia/d05b7ba7-1819-4990-ae98-38696bfef889/ss_CM2-KPCN",
            ["Horizon"] = "https://www.crestron.com/getmedia/1fa016cd-7291-4075-a406-c687dfa52608/ss_HZ2-KPCN",
            ["Palladiom"] = Lutron + "369881_eng.pdf",
            ["seeTouch"] = Lutron + "369353_hwqs_wired_arch.pdf",
            ["seeTouch Tabletop"] = Lutron + "369349.pdf",
            ["Signature"] = Lutron + "369668.pdf",
            ["Hybrid Repeater"] = Lutron + "HWQS_Hybrid_Repeater_369351a.pdf",
        };

    // Parts that are real orderable line items but carry no standalone cutsheet — documented on the
    // part they ship with. Kept out of PartUrls so a genuinely-forgotten URL is distinguishable.
    private static readonly HashSet<string> KnownNoCutsheet =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "PDW-QS-4", "PDW-QS-5", "PDW-QS-8", "PDW-QS-9", // QS wire harnesses
        };

    /// <summary>The cutsheet URL for a closed-set part number, or empty if it has none (unmapped or a
    /// known no-cutsheet part). Empty ⇒ the row builder drops it (no page).</summary>
    public static string ResolvePart(string partNumber)
        => partNumber != null && PartUrls.TryGetValue(partNumber.Trim(), out var url) ? url : string.Empty;

    /// <summary>The cutsheet URL for a placed keypad/repeater <c>Model</c>, or empty if the Model is
    /// blank or unmapped. Empty ⇒ a greyed "no cutsheet" row (still selectable for a local override).</summary>
    public static string ResolveModel(string model)
        => !string.IsNullOrWhiteSpace(model) && ModelUrls.TryGetValue(model.Trim(), out var url) ? url : string.Empty;

    /// <summary>True when a part number is deliberately accounted for — either mapped to a URL or in
    /// <see cref="KnownNoCutsheet"/>. The completeness test asserts this for every part a brand config
    /// can emit, so a new part added to <c>BrandConfig</c> with neither can never silently vanish.</summary>
    public static bool IsAccountedFor(string partNumber)
        => partNumber != null
           && (PartUrls.ContainsKey(partNumber.Trim()) || KnownNoCutsheet.Contains(partNumber.Trim()));
}
