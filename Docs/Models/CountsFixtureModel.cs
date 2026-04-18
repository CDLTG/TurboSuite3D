namespace TurboSuite.Docs.Models;

public class CountsFixtureModel
{
    public string TypeMark { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string[] CatalogNumbers { get; set; } = new string[6];
    public int Count { get; set; }
    public double LinearLength { get; set; }
    public double ReelLength { get; set; }
    public double ChannelLength { get; set; }
}
