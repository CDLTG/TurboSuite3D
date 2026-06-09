namespace TurboSuite.Setup.Services;

/// <summary>
/// Outcome of trying to apply the firm's linked-view + host-graphics hybrid to a view.
/// The hybrid uses <c>LinkVisibility.Custom</c>, which only the Revit 2025+ API can write — so
/// the implementation lives in a per-shim <c>LinkGraphicsSeam</c> (full in 2025, no-op in 2024).
/// Genuine Revit rejections surface as exceptions, not this enum.
/// </summary>
internal enum LinkGraphicsApplyResult
{
    /// <summary>Override written successfully (Revit 2025+).</summary>
    Applied,

    /// <summary>This Revit version can't set Custom link overrides (Revit 2024) — caller skips.</summary>
    NotSupportedInThisRevit
}
