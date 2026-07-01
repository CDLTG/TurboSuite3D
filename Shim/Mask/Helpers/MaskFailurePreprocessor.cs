using Autodesk.Revit.DB;

namespace TurboSuite.Mask.Helpers;

/// <summary>
/// Swallows the benign "a group has been changed outside group edit mode" warning Revit raises
/// when TurboMask groups the view-specific wire-overlay detail lines: at post-commit regen Revit
/// normalizes a member detail curve and flags it as an out-of-edit-mode change. Because the
/// TurboMask group only ever has a single instance, Revit already auto-allows the change — this
/// just deletes the warning so users aren't prompted. Nothing about connectivity, the region, or
/// the overlays is actually altered.
/// </summary>
public class MaskFailurePreprocessor : IFailuresPreprocessor
{
    private static readonly FailureDefinitionId GroupChangedOutsideEditModeId =
        BuiltInFailures.GroupFailures.AtomViolationWhenOnePlaceInstance;

    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        var failures = failuresAccessor.GetFailureMessages();

        foreach (FailureMessageAccessor failure in failures)
        {
            if (failure.GetSeverity() == FailureSeverity.Warning &&
                failure.GetFailureDefinitionId() == GroupChangedOutsideEditModeId)
            {
                failuresAccessor.DeleteWarning(failure);
            }
        }

        return FailureProcessingResult.Continue;
    }
}
