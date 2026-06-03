using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.Services;
using TurboSuite.Shared.Helpers;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Docs.ViewModels;

public class CountsViewModel : ViewModelBase
{
    private readonly DocsViewModel _parent;
    private List<CountsFixtureModel> _fixtures = [];
    private double _progress;
    private string _statusText = string.Empty;
    private bool _isGenerating;
    private bool _isUpdateMode;
    private string _repDirectoryPath = string.Empty;
    private string _notifyEmail = string.Empty;
    private string _headerImagePath = string.Empty;
    private string _footerImagePath = string.Empty;

    public string ProjectName { get; }
    public string ProjectNumber { get; }

    public int TypeCount => _fixtures.Count;
    public int TotalFixtureCount
    {
        get
        {
            int total = 0;
            foreach (var f in _fixtures) total += f.Count;
            return total;
        }
    }

    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool IsGenerating
    {
        get => _isGenerating;
        set
        {
            if (SetProperty(ref _isGenerating, value))
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsUpdateMode
    {
        get => _isUpdateMode;
        set => SetProperty(ref _isUpdateMode, value);
    }

    public RelayCommand GenerateCommand { get; }
    public RelayCommand BrowseRepDirectoryCommand { get; }
    public RelayCommand BrowseHeaderImageCommand { get; }
    public RelayCommand BrowseFooterImageCommand { get; }

    public string RepDirectoryPath
    {
        get => _repDirectoryPath;
        set => SetProperty(ref _repDirectoryPath, value);
    }

    public string NotifyEmail
    {
        get => _notifyEmail;
        set => SetProperty(ref _notifyEmail, value);
    }

    public string HeaderImagePath
    {
        get => _headerImagePath;
        set => SetProperty(ref _headerImagePath, value);
    }

    public string FooterImagePath
    {
        get => _footerImagePath;
        set => SetProperty(ref _footerImagePath, value);
    }

    public CountsViewModel(string projectName, string projectNumber, DocsViewModel parent)
    {
        _parent = parent;
        ProjectName = projectName;
        ProjectNumber = projectNumber ?? string.Empty;
        var settings = DocsSettingsService.Load();
        _repDirectoryPath = settings.RepDirectoryPath;
        _notifyEmail = settings.CountsNotifyEmail;
        _headerImagePath = settings.CountsHeaderImagePath;
        _footerImagePath = settings.CountsFooterImagePath;
        GenerateCommand = new RelayCommand(ExecuteGenerate, () => !IsGenerating && _fixtures.Count > 0);
        BrowseRepDirectoryCommand = new RelayCommand(ExecuteBrowseRepDirectory);
        BrowseHeaderImageCommand = new RelayCommand(() => BrowseImage(p => HeaderImagePath = p, HeaderImagePath));
        BrowseFooterImageCommand = new RelayCommand(() => BrowseImage(p => FooterImagePath = p, FooterImagePath));
    }

    public void LoadData(List<CountsFixtureModel> fixtures)
    {
        _fixtures = fixtures;
        OnPropertyChanged(nameof(TypeCount));
        OnPropertyChanged(nameof(TotalFixtureCount));
    }

    public void SaveSettings()
    {
        var settings = DocsSettingsService.Load();
        settings.RepDirectoryPath = _repDirectoryPath;
        settings.CountsNotifyEmail = _notifyEmail;
        settings.CountsHeaderImagePath = _headerImagePath;
        settings.CountsFooterImagePath = _footerImagePath;
        DocsSettingsService.Save(settings);
    }

    private static void BrowseImage(Action<string> setter, string current)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Image Files|*.png;*.jpg;*.jpeg",
            Title = "Select Image"
        };
        if (!string.IsNullOrWhiteSpace(current) && File.Exists(current))
            dlg.InitialDirectory = Path.GetDirectoryName(current);
        if (dlg.ShowDialog() == true)
            setter(dlg.FileName);
    }

    private void ExecuteBrowseRepDirectory()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Excel Files|*.xlsx;*.xlsm",
            Title = "Select Rep Directory Workbook"
        };
        if (!string.IsNullOrWhiteSpace(RepDirectoryPath) && File.Exists(RepDirectoryPath))
            dlg.InitialDirectory = Path.GetDirectoryName(RepDirectoryPath);
        if (dlg.ShowDialog() == true)
            RepDirectoryPath = dlg.FileName;
    }

    private async void ExecuteGenerate()
    {
        if (_fixtures.Count == 0) return;

        if (!ConfirmUnspecifiedTypes()) return;

        string? outputPath;

        if (IsUpdateMode)
        {
            var openDialog = new OpenFileDialog
            {
                Filter = "Excel Files|*.xlsx",
                Title = "Select Existing Counts Workbook"
            };
            if (openDialog.ShowDialog() != true) return;
            outputPath = openDialog.FileName;

            // The pricing team can keep this workbook open for days during an active
            // bid. Updating it in place needs exclusive access, so check up front and
            // ask them to close it rather than burning a long export only to fail on
            // save with an opaque IOException.
            if (FileLockHelper.IsFileLocked(outputPath))
            {
                string owner = FileLockHelper.TryGetLockOwner(outputPath);
                string who = string.IsNullOrWhiteSpace(owner) ? "another user" : owner;
                StatusText = $"Workbook is open ({who}). Ask them to close it, then try again.";
                System.Windows.MessageBox.Show(
                    $"{Path.GetFileName(outputPath)} is currently open by {who}.\n\n" +
                    "Please ask them to close the workbook, then run the update again.",
                    "TurboDocs — Counts workbook in use",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            // Updating rebuilds the Revit-owned sheets in place — counts, contractor
            // sheets, changes. It's destructive by design, so confirm the target file
            // before touching it to guard against an accidental update of the wrong
            // workbook. Default to No.
            var confirm = System.Windows.MessageBox.Show(
                $"Update {Path.GetFileName(outputPath)} with the current Revit counts?\n\n" +
                "This rebuilds the counts, contractor, and changes sheets in place. " +
                "Pricing entered by the team is preserved, but the Revit-owned data is overwritten.\n\n" +
                "Make sure this is the correct workbook before continuing.",
                "TurboDocs — Confirm Counts update",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;
        }
        else
        {
            var saveDialog = new SaveFileDialog
            {
                Filter = "Excel Files|*.xlsx",
                FileName = $"{ProjectName}_Counts.xlsx"
            };
            if (saveDialog.ShowDialog() != true) return;
            outputPath = saveDialog.FileName;
        }

        _parent.SaveSettings();
        IsGenerating = true;
        Progress = 0;

        try
        {
            bool updateMode = IsUpdateMode;
            var fixtures = _fixtures;
            string projName = ProjectName;
            string projNumber = ProjectNumber;
            string projLocation = _parent.ProjectLocation;
            string repDirPath = _repDirectoryPath;
            string headerImg = _headerImagePath;
            string footerImg = _footerImagePath;
            DateTime headerDate = _parent.HeaderDate;

            StatusText = updateMode ? "Updating workbook..." : "Generating workbook...";
            Progress = 30;

            await Task.Run(() =>
            {
                if (updateMode)
                    CountsWorkbookService.GenerateUpdate(fixtures, outputPath, repDirPath, headerDate, headerImg, footerImg);
                else
                    CountsWorkbookService.GenerateNew(fixtures, projName, projNumber, projLocation, outputPath, repDirPath, headerDate, headerImg, footerImg);
            });

            Progress = 100;
            StatusText = $"Done. Saved to {Path.GetFileName(outputPath)}";

            // Open pre-filled email notification if a notify email is configured (Revit-side setting)
            string notifyEmail = _notifyEmail;
            if (!string.IsNullOrWhiteSpace(notifyEmail))
            {
                string subject = Uri.EscapeDataString($"Counts Updated - {projName}");
                string body = Uri.EscapeDataString($"The counts workbook for {projName} has been updated.\n\n{outputPath}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"mailto:{notifyEmail}?subject={subject}&body={body}",
                    UseShellExecute = true
                });
            }
        }
        catch (IOException)
        {
            StatusText = "Error: File is open in another application. Please close it and try again.";
        }
        catch (CatalogQtyValidationException ex)
        {
            StatusText = "Export blocked: Catalog Qty validation failed.";
            System.Windows.MessageBox.Show(
                ex.Message,
                "TurboDocs — Catalog Qty validation",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        catch (CatalogLengthTokenValidationException ex)
        {
            StatusText = "Export blocked: Catalog Number length-token validation failed.";
            System.Windows.MessageBox.Show(
                ex.Message,
                "TurboDocs — Catalog Number length-token validation",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
        }
    }

    // Warn when any Type Mark has all six Catalog Number slots blank. Such types are
    // collected and counted but emit zero rows on the Worksheet — pricers see nothing,
    // design sees nothing, and the omission is silent. Surface them as a soft prompt
    // so the user can either cancel and finish the spec, or proceed knowing those
    // types won't appear on the quote.
    private bool ConfirmUnspecifiedTypes()
    {
        var offenders = new List<(string TypeMark, int InstanceCount)>();
        foreach (var f in _fixtures)
        {
            bool anySpec = false;
            for (int c = 0; c < 6; c++)
            {
                if (!string.IsNullOrWhiteSpace(f.CatalogNumbers[c])) { anySpec = true; break; }
            }
            if (!anySpec) offenders.Add((f.TypeMark, f.Count));
        }
        if (offenders.Count == 0) return true;

        offenders.Sort((a, b) => string.Compare(a.TypeMark, b.TypeMark, StringComparison.OrdinalIgnoreCase));

        const int maxListed = 20;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{offenders.Count} type(s) have no Catalog Numbers filled in and will be omitted from the Worksheet:");
        sb.AppendLine();
        int shown = Math.Min(offenders.Count, maxListed);
        for (int i = 0; i < shown; i++)
            sb.AppendLine($"  • {offenders[i].TypeMark}  ({offenders[i].InstanceCount} instance{(offenders[i].InstanceCount == 1 ? "" : "s")})");
        if (offenders.Count > maxListed)
            sb.AppendLine($"  … and {offenders.Count - maxListed} more");
        sb.AppendLine();
        sb.AppendLine("To include a type with no Catalog Numbers on the quote, enter a placeholder (e.g. \"TBD\") in Catalog Number1.");
        sb.AppendLine();
        sb.AppendLine("Generate anyway?");

        var result = System.Windows.MessageBox.Show(
            sb.ToString(),
            "TurboDocs — Unspecified types",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        return result == System.Windows.MessageBoxResult.Yes;
    }
}
