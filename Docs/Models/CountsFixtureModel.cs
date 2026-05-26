using System.Collections.Generic;

namespace TurboSuite.Docs.Models;

public class CountsFixtureModel
{
    public string TypeMark { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string[] CatalogNumbers { get; set; } = new string[6];
    public string[] CatalogQtys { get; set; } = new string[6];
    public int Count { get; set; }
    public double LinearLength { get; set; }
    public double ReelLength { get; set; }
    public double ChannelLength { get; set; }
    public string[] Notes { get; set; } = new string[6];

    // Per-instance Linear Length pooled to rounded inches. Keyed inches → instance count.
    // Used by Catalog NumberX {xx} token expansion; LinearLength sum stays for the
    // reel/channel Calc path.
    public Dictionary<int, int> LinearLengthBuckets { get; } = new();
}
