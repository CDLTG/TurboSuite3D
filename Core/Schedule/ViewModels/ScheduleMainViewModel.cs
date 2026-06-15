#nullable disable
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TurboSuite.Abstractions;
using TurboSuite.Schedule.Models;
using TurboSuite.Schedule.Services;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Schedule.ViewModels;

/// <summary>
/// TurboSchedule modeless ViewModel — page-per-Type-Mark spec editor. Edits accumulate in memory
/// (dirty tracking) across pages; nothing writes until Save, which flushes every dirty page in one
/// transaction via the work queue (Option A). Copy/Paste is in-VM (see <see cref="SpecClipboard"/>).
/// </summary>
public class ScheduleMainViewModel : ViewModelBase
{
    private readonly IRevitWorkQueue _workQueue;
    private readonly IScheduleWriter _writer;
    private SpecClipboard _clipboard;
    private FixtureTypeSpec _currentPage;
    private bool _isBusy;
    private string _statusMessage = "";

    public ScheduleMainViewModel(IReadOnlyList<FixtureTypeSpec> pages,
        IRevitWorkQueue workQueue, IScheduleWriter writer)
    {
        _workQueue = workQueue;
        _writer = writer;

        Pages = new ObservableCollection<FixtureTypeSpec>(pages);
        foreach (var p in Pages)
            p.DirtyChanged += _ => OnDirtyChanged();

        _currentPage = Pages.FirstOrDefault();

        PrevCommand = new RelayCommand(Prev, () => CurrentIndex > 0);
        NextCommand = new RelayCommand(Next, () => CurrentIndex >= 0 && CurrentIndex < Pages.Count - 1);
        SaveCommand = new RelayCommand(Save, () => !IsBusy && HasUnsavedChanges);
        DiscardCommand = new RelayCommand(DiscardAll, () => !IsBusy && HasUnsavedChanges);

        CopyTypeCommand = new RelayCommand(CopyType, () => _currentPage != null);
        CopySectionCommand = new RelayCommand<SpecSection>(CopySection, _ => _currentPage != null);
        PasteCommand = new RelayCommand(Paste, CanPaste);
        PasteSectionCommand = new RelayCommand<SpecSection>(PasteSection, CanPasteSection);
    }

    public ObservableCollection<FixtureTypeSpec> Pages { get; }

    public FixtureTypeSpec CurrentPage
    {
        get => _currentPage;
        set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(CurrentIndex));
                OnPropertyChanged(nameof(PagePositionText));
            }
        }
    }

    public int CurrentIndex => _currentPage == null ? -1 : Pages.IndexOf(_currentPage);
    public string PagePositionText =>
        Pages.Count == 0 ? "No types" : $"Type {CurrentIndex + 1} of {Pages.Count}";

    public bool HasUnsavedChanges => Pages.Any(p => p.IsDirty);
    public int DirtyCount => Pages.Count(p => p.IsDirty);
    public string SaveLabel => DirtyCount > 0 ? $"Save ({DirtyCount})" : "Save";
    public string UnsavedBadge => DirtyCount == 0 ? "" :
        $"● {DirtyCount} type{(DirtyCount == 1 ? "" : "s")} unsaved";

    public bool IsBusy
    {
        get => _isBusy;
        set { if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(IsEnabled)); }
    }
    public bool IsEnabled => !_isBusy;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public RelayCommand PrevCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public RelayCommand CopyTypeCommand { get; }
    public RelayCommand<SpecSection> CopySectionCommand { get; }
    public RelayCommand PasteCommand { get; }
    public RelayCommand<SpecSection> PasteSectionCommand { get; }

    private void OnDirtyChanged()
    {
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(DirtyCount));
        OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(UnsavedBadge));
    }

    private void Prev()
    {
        if (CurrentIndex > 0) CurrentPage = Pages[CurrentIndex - 1];
    }

    private void Next()
    {
        if (CurrentIndex >= 0 && CurrentIndex < Pages.Count - 1) CurrentPage = Pages[CurrentIndex + 1];
    }

    private void DiscardAll()
    {
        foreach (var page in Pages)
            foreach (var f in page.AllFields)
                f.ResetToOriginal();
        StatusMessage = "Discarded all unsaved changes.";
    }

    // ── Copy / paste ──

    private void CopyType()
    {
        if (_currentPage == null) return;
        _clipboard = new SpecClipboard
        {
            Scope = ClipboardScope.Type,
            SourceKind = _currentPage.Kind,
            Section = null,
            Values = SnapshotFields(_currentPage.AllFields),
            Descriptor = $"type {_currentPage.TypeMark}"
        };
        StatusMessage = $"Copied {_clipboard.Descriptor}";
    }

    private void CopySection(SpecSection section)
    {
        if (_currentPage == null) return;
        _clipboard = new SpecClipboard
        {
            Scope = ClipboardScope.Section,
            SourceKind = _currentPage.Kind,
            Section = section,
            Values = SnapshotFields(_currentPage.SectionFields(section)),
            Descriptor = SectionName(section)
        };
        StatusMessage = $"Copied {_clipboard.Descriptor}";
    }

    /// <summary>Only editable, non-varies fields are copyable (source-⟨varies⟩ has no single value).</summary>
    private static Dictionary<string, string> SnapshotFields(IEnumerable<SpecField> fields)
    {
        var map = new Dictionary<string, string>();
        foreach (var f in fields)
        {
            if (!f.IsEditable || f.IsVaries) continue;
            map[f.Def.ParamKey] = f.Value;
        }
        return map;
    }

    private bool CanPaste()
    {
        if (_clipboard == null || _currentPage == null) return false;
        // Whole-type paste is same-kind only; a section paste works cross-kind.
        return _clipboard.Scope == ClipboardScope.Section || _clipboard.SourceKind == _currentPage.Kind;
    }

    private void Paste()
    {
        if (!CanPaste()) return;
        int n = ApplyClipboard(_clipboard.Values);
        StatusMessage = _clipboard.Scope == ClipboardScope.Type
            ? $"Pasted type from {_clipboard.SourceKind} ({n} fields)"
            : $"Pasted {_clipboard.Descriptor} ({n} fields)";
    }

    private bool CanPasteSection(SpecSection section) =>
        _clipboard != null && _currentPage != null &&
        _clipboard.Scope == ClipboardScope.Section && _clipboard.Section == section;

    private void PasteSection(SpecSection section)
    {
        if (!CanPasteSection(section)) return;
        int n = ApplyClipboard(_clipboard.Values);
        StatusMessage = $"Pasted {SectionName(section)} ({n} fields)";
    }

    /// <summary>Writes clipboard values into matching current-page fields as dirty edits, skipping
    /// locked/n/a targets and fields absent on the target (non-overlap silently skipped).</summary>
    private int ApplyClipboard(Dictionary<string, string> values)
    {
        if (_currentPage == null) return 0;
        var byKey = _currentPage.AllFields.ToDictionary(f => f.Def.ParamKey, f => f);
        int n = 0;
        foreach (var kv in values)
        {
            if (!byKey.TryGetValue(kv.Key, out var target)) continue;
            if (!target.IsEditable) continue;
            target.ApplyPaste(kv.Value);
            n++;
        }
        return n;
    }

    private static string SectionName(SpecSection s) => s switch
    {
        SpecSection.Identity => "Identity",
        SpecSection.Electrical => "Electrical",
        SpecSection.Mechanical => "Mechanical",
        SpecSection.Photometric => "Photometric",
        SpecSection.Notes => "Notes",
        _ => s.ToString()
    };

    // ── Save ──

    private void Save()
    {
        if (IsBusy || !HasUnsavedChanges) return;

        var requests = new List<SpecWriteRequest>();
        foreach (var page in Pages.Where(p => p.IsDirty))
        {
            var req = new SpecWriteRequest { TypeMark = page.TypeMark, Kind = page.Kind };
            foreach (var f in page.AllFields.Where(f => f.IsDirty))
            {
                req.Fields.Add(new SpecFieldWrite
                {
                    Label = f.Label,
                    ParamKey = f.Def.ParamKey,
                    IsBuiltIn = f.Def.IsBuiltIn,
                    Value = f.Value
                });
            }
            if (req.Fields.Count > 0) requests.Add(req);
        }

        IsBusy = true;
        StatusMessage = "Saving…";
        _workQueue.Enqueue(
            () => _writer.Write(requests),
            result =>
            {
                try
                {
                    if (result is ScheduleWriteResult r)
                    {
                        foreach (var page in Pages)
                            foreach (var f in page.AllFields.Where(f => f.IsDirty))
                            {
                                var key = ScheduleWriteKey.For(page.TypeMark, page.Kind, f.Def.ParamKey);
                                if (r.SavedKeys.Contains(key)) f.MarkSaved();
                            }

                        StatusMessage = r.Skipped.Count == 0
                            ? $"Updated {r.UpdatedTypes} type(s)."
                            : $"Updated {r.UpdatedTypes} type(s); skipped {r.Skipped.Count} field(s): {string.Join(", ", r.Skipped.Take(6))}{(r.Skipped.Count > 6 ? "…" : "")}";
                    }
                    else
                    {
                        StatusMessage = "Save failed.";
                    }
                }
                finally
                {
                    IsBusy = false;
                    OnDirtyChanged();
                }
            });
    }
}
