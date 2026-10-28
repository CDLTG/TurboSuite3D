#nullable disable
using System;
using Autodesk.Revit.DB;
using TurboSuite.Setup.Services;

namespace TurboSuite.Setup
{
    /// <summary>
    /// Revit 2026 implementation of the link-graphics seam. Reproduces the firm's manual
    /// "RVT Link Display Settings → Custom" hybrid: the architect view is the linked view, but the
    /// link's category visibility and graphics, color fill, filters and view range all follow the
    /// host view — so the firm template baked onto the view decides what the link shows.
    ///
    /// This file is compiled ONLY into the Revit 2026 shim (it lives under Revit2026/, outside the
    /// shared Shim/ cone). It uses members — ObjectStyles, ColorFill, ViewFilterType, ViewRange,
    /// NestedLinks — that exist only in the 2025+ API, so it must never reach the net48 build.
    /// The Revit2024/ copy of this class is a no-op stub; the Revit2025/ copy is identical to this.
    /// </summary>
    internal static class LinkGraphicsSeam
    {
        public static LinkGraphicsApplyResult ApplyFirmHybrid(
            View view, ElementId linkElementId, ElementId linkedViewId)
        {
            var settings = new RevitLinkGraphicsSettings
            {
                LinkVisibilityType = LinkVisibility.Custom,
                LinkedViewId = linkedViewId,

                // Model categories follow the HOST view — on/off as well as graphics (verified in-model,
                // Revit 2025: AL_Spaces turning Rooms on shows the link's rooms, and toggling the host
                // Rooms category toggles them). This is what lets a template drive the link, e.g. the
                // Toposolid hide and AL_Spaces' Rooms. Flipping the dropdown to <Custom> by hand later
                // keeps the host's settings as of that moment, just making them editable.
                // NOTE: ObjectStyles = Custom is NOT settable — verified on Revit 2025, not just 2024.
                // The "Model categories" dropdown maps to this property; setting it to Custom requires
                // per-category override data the API can't supply, so SetLinkOverrides rejects the whole
                // settings object and the link falls back off Custom (Basics tab included). Flipping that
                // dropdown to <Custom> to re-style the firm's 4 working categories stays a manual step.
                ObjectStyles = LinkVisibility.ByHostView,
                ColorFill = LinkVisibility.ByHostView,
                ViewFilterType = LinkVisibility.ByHostView,

                // View range follows the HOST view — TurboSetup has already copied the architect's
                // range onto the host view (see ViewRangeService), making it the single source of
                // truth.
                ViewRange = LinkVisibility.ByHostView,
                NestedLinks = LinkVisibility.ByLinkView,
            };

            view.SetLinkOverrides(linkElementId, settings);

            // Trust nothing: read back what Revit actually stored. A silently-rejected Custom
            // would leave the link on the host-view default while reporting no error.
            var stored = view.GetLinkOverrides(linkElementId);
            if (stored == null || stored.LinkVisibilityType != LinkVisibility.Custom)
                throw new InvalidOperationException(
                    "link override did not persist as Custom (read back as " +
                    (stored == null ? "null" : stored.LinkVisibilityType.ToString()) + ").");

            return LinkGraphicsApplyResult.Applied;
        }
    }
}
