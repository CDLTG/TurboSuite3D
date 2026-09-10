#nullable enable
using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace TurboSuite.Shared.Services;

/// <summary>
/// The single source of truth for "is this a shade?" — one shade-motor identity shared by every
/// module that needs to separate shade circuits from lighting loads. Consumers:
/// <list type="bullet">
///   <item><c>ZonesCollectorService</c> drops shade circuits so their motors don't become a spurious
///   lighting zone; <c>ShadeCircuitCollectorService</c> / <c>ShadeDemandProvider</c> pick them up.</item>
///   <item><c>LoadsCollectorService</c> (TurboDocs Load Schedule) drops them — shades aren't a designed
///   electrical load and carry no accurate wattage, so they don't belong on a load schedule. They
///   remain on the Panel Schedule, where every breaker is listed.</item>
///   <item>TurboWire routes them with the shade-specific wiring path.</item>
/// </list>
///
/// <b>Identity — the shade motor, not the panel.</b> A circuit is a shade circuit when a connected
/// fixture is a shade motor, matched by family name containing <see cref="ShadeMotorFamilyToken"/>
/// ("Shade Motor" — catches both the 3D <c>AL_Electrical Fixture_Shade Motor</c> and the 2D
/// <c>Shade Motor</c>). A shade motor is an Electrical Fixture, which the lighting collectors would
/// otherwise treat as a lighting load.
/// </summary>
public static class ShadeCircuitClassifier
{
    /// <summary>Family-name token identifying a shade motor. A substring, case-insensitive, so it
    /// catches both authored families (<c>AL_Electrical Fixture_Shade Motor</c> and <c>Shade
    /// Motor</c>) and future variants that keep the words.</summary>
    public const string ShadeMotorFamilyToken = "Shade Motor";

    /// <summary>A circuit carrying shade motors — the hook the lighting collectors use to drop shade
    /// circuits before their motors become a spurious lighting load.</summary>
    public static bool IsShadeCircuit(ElectricalSystem circuit) => CountShadeMotors(circuit) > 0;

    public static bool IsShadeMotor(FamilyInstance fi)
    {
        string family = fi?.Symbol?.Family?.Name ?? string.Empty;
        return family.IndexOf(ShadeMotorFamilyToken, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static int CountShadeMotors(ElectricalSystem circuit)
    {
        if (circuit?.Elements == null) return 0;
        int count = 0;
        foreach (Element el in circuit.Elements)
            if (el is FamilyInstance fi && IsShadeMotor(fi))
                count++;
        return count;
    }
}
