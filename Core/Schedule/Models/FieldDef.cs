#nullable disable
using System;
using System.Collections.Generic;
using TurboSuite.Dmx;
using TurboSuite.Shared.Constants;

namespace TurboSuite.Schedule.Models;

/// <summary>Form section a field renders under.</summary>
public enum SpecSection
{
    Identity,
    Electrical,
    Mechanical,
    Photometric,
    Notes
}

/// <summary>Which page kinds a field applies to.</summary>
[Flags]
public enum SpecKinds
{
    None = 0,
    Fixture = 1,
    Driver = 2,
    Both = Fixture | Driver
}

/// <summary>Sub-role for fields that pair/loop in the form (catalog # ↔ qty, notes 1–6).</summary>
public enum FieldRole
{
    Normal,
    CatalogNumber,
    CatalogQty,
    Note
}

/// <summary>
/// One immutable field descriptor in the data-driven roster. The collector, writer, and XAML all
/// iterate <see cref="Roster"/> — adding/removing a field is a one-line edit here. <see cref="ParamKey"/>
/// is a Revit built-in parameter <i>enum name</i> when <see cref="IsBuiltIn"/> (the shim parses it to
/// <c>BuiltInParameter</c>), otherwise a shared/custom parameter name fed to <c>LookupParameter</c>.
/// </summary>
public class FieldDef
{
    public string Label { get; }
    public SpecSection Section { get; }
    public string ParamKey { get; }
    public bool IsBuiltIn { get; }
    public SpecKinds Kinds { get; }
    public FieldRole Role { get; }
    public int Slot { get; }

    /// <summary>True for URL-valued fields — the form shows a click-to-open glyph that launches the browser.</summary>
    public bool IsUrl { get; }

    public FieldDef(string label, SpecSection section, string paramKey, SpecKinds kinds,
        bool isBuiltIn = false, FieldRole role = FieldRole.Normal, int slot = 0, bool isUrl = false)
    {
        Label = label;
        Section = section;
        ParamKey = paramKey;
        IsBuiltIn = isBuiltIn;
        Kinds = kinds;
        Role = role;
        Slot = slot;
        IsUrl = isUrl;
    }

    public bool AppliesTo(PageKind kind)
    {
        var flag = kind == PageKind.Fixture ? SpecKinds.Fixture : SpecKinds.Driver;
        return (Kinds & flag) != 0;
    }

    /// <summary>
    /// Pinned, ordered roster (sources: <c>Specs/ScheduleParameters.txt</c> fixtures,
    /// <c>Specs/ScheduleDriverParameters.txt</c> drivers). Type Mark is intentionally absent —
    /// it is the page key / read-only header, not an editable field.
    /// </summary>
    public static readonly IReadOnlyList<FieldDef> Roster = BuildRoster();

    private static IReadOnlyList<FieldDef> BuildRoster()
    {
        var list = new List<FieldDef>();

        // ── Identity (both kinds, identical) ──
        list.Add(new FieldDef("Classification", SpecSection.Identity, ParameterNames.Classification, SpecKinds.Both));
        list.Add(new FieldDef("Model", SpecSection.Identity, "ALL_MODEL_MODEL", SpecKinds.Both, isBuiltIn: true));
        list.Add(new FieldDef("Manufacturer", SpecSection.Identity, "ALL_MODEL_MANUFACTURER", SpecKinds.Both, isBuiltIn: true));
        list.Add(new FieldDef("Description 1", SpecSection.Identity, "ALL_MODEL_DESCRIPTION", SpecKinds.Both, isBuiltIn: true));
        list.Add(new FieldDef("Description 2", SpecSection.Identity, ParameterNames.Description2, SpecKinds.Both));
        list.Add(new FieldDef("URL", SpecSection.Identity, "ALL_MODEL_URL", SpecKinds.Both, isBuiltIn: true, isUrl: true));
        list.Add(new FieldDef("Data Sheet URL", SpecSection.Identity, ParameterNames.DataSheetUrl, SpecKinds.Both, isUrl: true));
        for (int c = 1; c <= 6; c++)
        {
            list.Add(new FieldDef($"Catalog #{c}", SpecSection.Identity, $"Catalog Number{c}", SpecKinds.Both, role: FieldRole.CatalogNumber, slot: c));
            list.Add(new FieldDef($"Qty {c}", SpecSection.Identity, $"Catalog Qty{c}", SpecKinds.Both, role: FieldRole.CatalogQty, slot: c));
        }

        // ── Electrical ──
        list.Add(new FieldDef("Power", SpecSection.Electrical, ParameterNames.Power, SpecKinds.Both));
        list.Add(new FieldDef("Power/Length", SpecSection.Electrical, ParameterNames.PowerPerLength, SpecKinds.Fixture));
        list.Add(new FieldDef("Voltage", SpecSection.Electrical, ParameterNames.Voltage, SpecKinds.Both));
        list.Add(new FieldDef("Dimming Protocol", SpecSection.Electrical, ParameterNames.DimmingProtocol, SpecKinds.Both));
        list.Add(new FieldDef("Dimming Range", SpecSection.Electrical, ParameterNames.DimmingRange, SpecKinds.Both));
        list.Add(new FieldDef("Remote Power Supply", SpecSection.Electrical, ParameterNames.RemotePowerSupply, SpecKinds.Fixture));
        list.Add(new FieldDef("Sub-Driver Power", SpecSection.Electrical, ParameterNames.SubDriverPower, SpecKinds.Driver));
        list.Add(new FieldDef("Maximum Fixtures", SpecSection.Electrical, ParameterNames.MaximumFixtures, SpecKinds.Driver));
        list.Add(new FieldDef("Derating Factor", SpecSection.Electrical, ParameterNames.DeratingFactor, SpecKinds.Driver));
        // DMX (net-new TurboDMX type params, grouped at the bottom of Electrical). Names come from
        // DmxParameterNames so they can't drift from what the DMX model reader keys on. DMX Channels is
        // shared fixture+decoder; Bundle Size is a fixture-type trait; Amps Per Channel is a decoder cap
        // (decoders are OST_LightingDevices, so it surfaces on the Driver page — n/a on non-DMX types).
        list.Add(new FieldDef("DMX Channels", SpecSection.Electrical, DmxParameterNames.DmxChannels, SpecKinds.Both));
        list.Add(new FieldDef("DMX Bundle Size", SpecSection.Electrical, DmxParameterNames.BundleSize, SpecKinds.Fixture));
        list.Add(new FieldDef("Amps Per Channel", SpecSection.Electrical, DmxParameterNames.DecoderAmpsPerChannel, SpecKinds.Driver));

        // ── Mechanical (driver drops Ceiling Thickness) ──
        list.Add(new FieldDef("Listings & Ratings", SpecSection.Mechanical, ParameterNames.ListingsAndRatings, SpecKinds.Both));
        list.Add(new FieldDef("Finish 1", SpecSection.Mechanical, ParameterNames.Finish1, SpecKinds.Both));
        list.Add(new FieldDef("Finish 2", SpecSection.Mechanical, ParameterNames.Finish2, SpecKinds.Both));
        list.Add(new FieldDef("Mounting", SpecSection.Mechanical, ParameterNames.Mounting, SpecKinds.Both));
        list.Add(new FieldDef("Ceiling Thickness", SpecSection.Mechanical, ParameterNames.CeilingThickness, SpecKinds.Fixture));

        // ── Photometric (fixtures only) ──
        list.Add(new FieldDef("Lumens", SpecSection.Photometric, ParameterNames.Lumens, SpecKinds.Fixture));
        list.Add(new FieldDef("Efficacy", SpecSection.Photometric, ParameterNames.LumenEfficacy, SpecKinds.Fixture));
        list.Add(new FieldDef("Beam °", SpecSection.Photometric, ParameterNames.BeamAngle, SpecKinds.Fixture));
        list.Add(new FieldDef("CBCP", SpecSection.Photometric, ParameterNames.Cbcp, SpecKinds.Fixture));
        list.Add(new FieldDef("CCT", SpecSection.Photometric, ParameterNames.Cct, SpecKinds.Fixture));
        list.Add(new FieldDef("CRI", SpecSection.Photometric, ParameterNames.Cri, SpecKinds.Fixture));
        list.Add(new FieldDef("SDCM", SpecSection.Photometric, ParameterNames.Sdcm, SpecKinds.Fixture));
        list.Add(new FieldDef("Rf", SpecSection.Photometric, ParameterNames.Rf, SpecKinds.Fixture));
        list.Add(new FieldDef("Rg", SpecSection.Photometric, ParameterNames.Rg, SpecKinds.Fixture));

        // ── Schedule Notes (both kinds) ──
        for (int n = 1; n <= 6; n++)
            list.Add(new FieldDef($"Note {n}", SpecSection.Notes, $"Schedule Notes{n}", SpecKinds.Both, role: FieldRole.Note, slot: n));

        return list;
    }
}
