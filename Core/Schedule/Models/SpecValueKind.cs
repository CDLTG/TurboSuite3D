namespace TurboSuite.Schedule.Models;

/// <summary>
/// Core-local stand-in for Revit's <c>StorageType</c> — Core has no Revit reference, so it cannot
/// hold <c>Autodesk.Revit.DB.StorageType</c> directly. The shim collector reads each param's live
/// <c>StorageType</c> and maps it here (<c>String</c> → <see cref="Text"/>, <c>Double</c>/
/// <c>Integer</c> → <see cref="Numeric"/>). Drives the display/edit path; the writer re-reads the
/// real <c>StorageType</c> at save time, so this brand is display-only.
/// </summary>
public enum SpecValueKind
{
    Text,
    Numeric,

    /// <summary>A Yes/No (Integer) param: rendered as a checkbox; value carried as "1"/"0".</summary>
    Boolean
}
