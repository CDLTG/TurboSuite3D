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

    // Default local PDF paths — keyed by catalog number, persists across projects
    public Dictionary<string, string> DefaultLocalPdfPaths { get; set; } = new();

    // Schedule tab settings
    public List<string> ScheduleSelectedTypeMarks { get; set; } = new();
    public bool ScheduleUseLargeFormat { get; set; }

    // Schedule tab — Specification Notes
    public List<string> SpecificationNotes { get; set; } = new();

    // Load Schedule tab settings
    public string LoadsSelectedSortColumn { get; set; } = "CircuitNumber";

    // Power Supplies tab settings
    public List<string> RPSSelectedTypeMarks { get; set; } = new();
    public int RPSOutputMode { get; set; }              // 0=Schedule, 1=Lookup, 2=Both
    public List<string> RPSSpecificationNotes { get; set; } = new();
    public bool RPSUseLargeFormat { get; set; }

    // Window state
    public int SelectedTabIndex { get; set; }

    // Cover page settings
    public string CoverBrandingVerticalPath { get; set; } = string.Empty;
    public string CoverBrandingHorizontalPath { get; set; } = string.Empty;
    public string ProjectLocation { get; set; } = string.Empty;

    // Counts tab — Rep Directory external workbook (drives Rep Lists sheet generation)
    public string RepDirectoryPath { get; set; } = string.Empty;

    // Counts tab — optional email address; Generate/Update opens a pre-filled mailto draft to this address
    public string CountsNotifyEmail { get; set; } = string.Empty;
}
