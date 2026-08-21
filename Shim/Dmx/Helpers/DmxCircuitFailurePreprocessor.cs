using System;
using Autodesk.Revit.DB;

namespace TurboSuite.Dmx.Helpers;

/// <summary>
/// Swallows the "Total Connected Apparent Power for Circuit &lt;unnamed&gt; is exceeding 80% of the
/// defined rating (20 A)" warning that Revit raises when TurboDMX packs a whole Control Zone's tape
/// onto its one <c>&lt;unnamed&gt;</c> power circuit. That over-amp is expected here — a DMX zone
/// circuit exists only to total the zone's load for the Load Schedule and let TurboZones name it, it
/// is never wired to a 20 A breaker — so the warning is pure noise on every Place. Scoped to the DMX
/// zone-circuit create transaction alone (passed into <see cref="TurboSuite.Shared.Services.CircuitService.CreateCircuit"/>
/// only by <c>DmxPlacementService.CircuitZones</c>); TurboWire / TurboDriver still surface it normally.
///
/// Keyed on the warning's real <see cref="FailureDefinitionId"/> GUID, captured from the running model
/// via a TurboSpike logging probe (not a guessed BuiltInFailures constant) so it's robust across Revit
/// versions. Only this exact warning is deleted; every other failure passes through untouched.
/// </summary>
public class DmxCircuitFailurePreprocessor : IFailuresPreprocessor
{
    // "...exceeding 80% of the defined rating (20 A)" — TurboSpike-captured, Revit 2025.
    private static readonly Guid OverRatingWarningGuid =
        new Guid("dae5d6e7-5a4f-4a9b-82f0-16873d2abf21");

    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
        {
            if (failure.GetSeverity() == FailureSeverity.Warning &&
                failure.GetFailureDefinitionId().Guid == OverRatingWarningGuid)
            {
                failuresAccessor.DeleteWarning(failure);
            }
        }

        return FailureProcessingResult.Continue;
    }
}
