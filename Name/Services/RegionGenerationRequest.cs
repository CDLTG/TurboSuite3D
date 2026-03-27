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
