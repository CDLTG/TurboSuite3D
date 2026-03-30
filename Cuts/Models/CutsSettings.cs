using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Cuts.Models;

public class CutsSettings
{
    public string LogoFilePath { get; set; } = string.Empty;
    public string CompanyAddress { get; set; } = string.Empty;
    public string CompanyPhone { get; set; } = string.Empty;
    public string CompanyEmail { get; set; } = string.Empty;
    public string CompanyWebsite { get; set; } = string.Empty;
    public string HeaderDate { get; set; } = string.Empty;
    public Dictionary<string, string> LocalPdfPaths { get; set; } = new();
    public List<string> SelectedTypeMarks { get; set; } = new();
}
