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

    // Release date stamped at the left of footered deliverables (yyyy.MM.dd), mirroring the
    // page-number position. Set by each footered export's viewmodel from the shared HeaderDate.
    public string FooterDate { get; set; } = string.Empty;
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

    // Legacy single-select output mode (0=Schedule, 1=Lookup, 2=Both). Superseded by the
    // three independent Include* checkboxes below; retained only so the one-time migration in
    // PowerSuppliesViewModel.LoadData can seed them for existing users. Do not read directly.
    public int RPSOutputMode { get; set; }

    // Output selection — independent checkboxes; any combination merges into one PDF.
    public bool RPSIncludeSchedule { get; set; }
    public bool RPSIncludeLookup { get; set; }
    public bool RPSIncludeBreakdown { get; set; }
    // False until RPSOutputMode has been migrated into the Include* flags (once per user).
    public bool RPSOutputMigrated { get; set; }

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

    // Counts tab — header/footer images embedded on Quote and Phase 1/2/3 sheets
    public string CountsHeaderImagePath { get; set; } = string.Empty;
    public string CountsFooterImagePath { get; set; } = string.Empty;

    // Counts tab — vertical banner floated top-left on the Excel Cover sheet (PNG/JPEG only;
    // Excel cannot embed a PDF). The cover's bottom banner reuses CountsFooterImagePath.
    // Distinct from CoverBrandingVerticalPath, which feeds the PDF cover.
    public string CountsCoverVerticalPath { get; set; } = string.Empty;
}
