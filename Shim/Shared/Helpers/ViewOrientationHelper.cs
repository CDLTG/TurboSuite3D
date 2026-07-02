using Autodesk.Revit.DB;

namespace TurboSuite.Shared.Helpers;

/// <summary>
/// Maps screen-relative offsets (as the user sees them in a plan/RCP view) into model
/// coordinates, honoring the view's crop rotation.
///
/// Placements anchored to a fixture, a wall face, or a line are already view-correct because
/// their frame rotates with the model geometry. This helper is only for placements expressed
/// in raw screen directions (e.g. "stack the next item DOWN the page") that would otherwise be
/// pinned to the model axes and drift when the view's crop is rotated.
///
/// Phase 0 findings (see plan): for both Floor Plan and RCP, <c>View.RightDirection</c> /
/// <c>View.UpDirection</c> track the crop rotation exactly and need no RCP sign special-case.
/// In an un-rotated view Right = (1,0,0) and Up = (0,1,0), so the mapping is the identity —
/// existing behavior is preserved byte-for-byte.
/// </summary>
public static class ViewOrientationHelper
{
    /// <summary>
    /// Converts a screen-relative offset (X = screen-right, Y = screen-up) into a model-space
    /// vector for the given view. The Z component is always zero — screen offsets never change
    /// elevation — so callers can add the result to a point without disturbing its height.
    /// </summary>
    public static XYZ ScreenOffsetToModel(View view, XYZ screenOffset)
    {
        XYZ right = view.RightDirection;
        XYZ up = view.UpDirection;
        double x = screenOffset.X * right.X + screenOffset.Y * up.X;
        double y = screenOffset.X * right.Y + screenOffset.Y * up.Y;
        return new XYZ(x, y, 0);
    }

    /// <summary>
    /// The view's crop rotation about the vertical axis, in radians (0 for an un-rotated view).
    /// Rotating a newly placed instance by this amount makes it read upright on screen — i.e.
    /// appear exactly as it does at model-rotation 0 in an un-rotated view. Identity at 0.
    /// </summary>
    public static double GetViewRotation(View view)
    {
        XYZ right = view.RightDirection;
        return System.Math.Atan2(right.Y, right.X);
    }
}
