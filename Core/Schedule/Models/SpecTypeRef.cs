#nullable disable
namespace TurboSuite.Schedule.Models;

/// <summary>
/// One Revit type (FamilySymbol) behind a Type Mark page — display-only identity for the header and the
/// ⟨varies⟩/n/a field tooltips. A Type Mark's group holds <b>every</b> loaded type carrying the mark,
/// placed or not (a Type Mark is one specification: its types may not disagree), so
/// <see cref="IsPlaced"/> lets the UI call out a type with no instances in the model.
/// </summary>
public sealed class SpecTypeRef
{
    public SpecTypeRef(string familyName, string typeName, bool isPlaced)
    {
        FamilyName = familyName ?? "";
        TypeName = typeName ?? "";
        IsPlaced = isPlaced;
    }

    public string FamilyName { get; }
    public string TypeName { get; }
    public bool IsPlaced { get; }

    /// <summary>"Family: Type", suffixed " (not placed)" for a type with no instances.</summary>
    public string Label => $"{FamilyName}: {TypeName}{(IsPlaced ? "" : " (not placed)")}";
}
