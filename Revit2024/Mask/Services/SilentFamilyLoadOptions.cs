using Autodesk.Revit.DB;

namespace TurboSuite.Mask.Services;

/// <summary>
/// IFamilyLoadOptions that silently overwrites any same-named family in the project.
/// Used by StampFamilyService when loading extracted Stamp_* annotation families.
/// </summary>
internal sealed class SilentFamilyLoadOptions : IFamilyLoadOptions
{
    public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
    {
        overwriteParameterValues = false;
        return true;
    }

    public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
        out FamilySource source, out bool overwriteParameterValues)
    {
        source = FamilySource.Family;
        overwriteParameterValues = false;
        return true;
    }
}
