#nullable disable
using System;

namespace TurboSuite.Name.Services;

public abstract class RegionGenerationRequest
{
    public Action<object> OnComplete { get; set; }
}

/// <summary>Rectangle mode: two-click pick loop.</summary>
public class RectanglePickRequest : RegionGenerationRequest { }

/// <summary>Polygon mode: multi-click pick loop, Escape closes current polygon.</summary>
public class PolygonPickRequest : RegionGenerationRequest { }

/// <summary>
/// Auto-generate mode: one-shot watershed partition of the whole floor from CAD room labels.
/// Runs the pipeline once and reports diagnostics (leaks / collision px / doors sealed) — no pick loop.
/// </summary>
public class AutoGeneratePickRequest : RegionGenerationRequest { }

/// <summary>Status update sent from a pick/generate handler to the ViewModel during/after the loop.</summary>
public record PickLoopUpdate(int TotalCreated, int TotalFailed, bool LoopEnded, string LastStatus = null);
