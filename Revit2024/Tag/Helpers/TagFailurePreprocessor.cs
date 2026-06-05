using Autodesk.Revit.DB;

namespace TurboSuite.Tag.Helpers;

public class TagFailurePreprocessor : IFailuresPreprocessor
{
    private static readonly FailureDefinitionId TagOverlapsId =
        BuiltInFailures.OverlapFailures.DuplicateInstances;

    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        var failures = failuresAccessor.GetFailureMessages();

        foreach (FailureMessageAccessor failure in failures)
        {
            if (failure.GetSeverity() == FailureSeverity.Warning &&
                failure.GetFailureDefinitionId() == TagOverlapsId)
            {
                failuresAccessor.DeleteWarning(failure);
            }
        }

        return FailureProcessingResult.Continue;
    }
}
