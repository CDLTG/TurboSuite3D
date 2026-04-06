using System.Collections.Generic;
using System.Linq;

namespace TurboSuite.Docs.Models;

public class DocsSettings
{
    public string LogoFilePath { get; set; } = string.Empty;
    public string CompanyAddress { get; set; } = string.Empty;
    public string CompanyPhone { get; set; } = string.Empty;
    public string CompanyEmail { get; set; } = string.Empty;
    public string CompanyWebsite { get; set; } = string.Empty;
    public string HeaderDate { get; set; } = string.Empty;
    public Dictionary<string, string> LocalPdfPaths { get; set; } = new();
    public List<string> SelectedTypeMarks { get; set; } = new();

    // Schedule tab settings
    public List<string> ScheduleSelectedTypeMarks { get; set; } = new();
    public bool ScheduleUseLargeFormat { get; set; }

    // Load Schedule tab settings
    public string LoadsSelectedSortColumn { get; set; } = "CircuitNumber";
}
