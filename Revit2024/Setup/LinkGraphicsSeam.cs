#nullable disable
using Autodesk.Revit.DB;
using TurboSuite.Setup.Services;

namespace TurboSuite.Setup
{
    /// <summary>
    /// Revit 2024 stub of the link-graphics seam. The 2024 API can't set
    /// <c>LinkVisibility.Custom</c> overrides (only ByHostView / ByLinkView), so the firm's
    /// linked-view + host-graphics hybrid isn't reproducible. TurboSetup still copies levels,
    /// creates views, and applies templates; users configure the link display manually.
    ///
    /// This file is compiled ONLY into the Revit 2024 shim. Its 2025 counterpart does the real work.
    /// </summary>
    internal static class LinkGraphicsSeam
    {
        public static LinkGraphicsApplyResult ApplyFirmHybrid(
            View view, ElementId linkElementId, ElementId linkedViewId)
            => LinkGraphicsApplyResult.NotSupportedInThisRevit;
    }
}
