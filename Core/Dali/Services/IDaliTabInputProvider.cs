#nullable enable
namespace TurboSuite.Dali.Services
{
    /// <summary>Revit-free contract for (re-)collecting TurboDALI's loop-declaration inputs from the model —
    /// the DALI zones + load counts, the panel-ZONE list, and the persisted loops. Called once at window open and
    /// again on Refresh, both through the work queue so the read runs on the Revit API thread.</summary>
    public interface IDaliTabInputProvider
    {
        DaliTabInputs Read();
    }
}
