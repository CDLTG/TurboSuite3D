#nullable disable
using System;

namespace TurboSuite.Name.Services;

public abstract class RegionGenerationRequest
{
    public Action<object> OnComplete { get; set; }
}

/// <summary>First run or resume: enter the PickPoint loop.</summary>
public class StartPickingRequest : RegionGenerationRequest { }
