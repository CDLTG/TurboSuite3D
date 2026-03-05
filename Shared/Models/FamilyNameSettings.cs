using System;
using System.Collections.Generic;
using TurboSuite.Bubble.Constants;

namespace TurboSuite.Shared.Models;

public class FamilyNameSettings
{
    public HashSet<string> WallSconceFamilies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ReceptacleFamilies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ElectricalVerticalFamilies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static FamilyNameSettings CreateDefaults() => new()
    {
        WallSconceFamilies = new(StringComparer.OrdinalIgnoreCase)
        {
            "AL_Decorative_Wall Sconce (Hosted)",
            "Z_Wall Sconce"
        },
        ReceptacleFamilies = new(StringComparer.OrdinalIgnoreCase)
        {
            "AL_Electrical Fixture_Receptacle (Hosted)",
            "Receptacle"
        },
        ElectricalVerticalFamilies = new(BubbleConstants.ElectricalVerticalFamilies, StringComparer.OrdinalIgnoreCase)
    };
}
