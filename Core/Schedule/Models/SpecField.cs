#nullable disable
using System;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Schedule.Models;

/// <summary>
/// One editable spec field on a page, reconciled across every symbol sharing the page's Type Mark.
/// Four mutually-exclusive display states, checked in this priority order:
/// <list type="bullet">
/// <item><b>n/a</b> (<see cref="IsNa"/>) — param absent on a symbol in the group (or storage-type
/// disagreement); a family-authoring error. Non-editable, never dirty, never written.</item>
/// <item><b>read-only</b> (<see cref="IsReadOnly"/>) — formula/API read-only on some symbol; greyed
/// 🔒, has a real value, just can't be written.</item>
/// <item><b>varies</b> (<see cref="IsVaries"/>) — symbols disagree; blank placeholder. Untouched it
/// never counts dirty (so Save never flattens legitimate per-symbol differences).</item>
/// <item><b>normal</b> — editable, agreed value.</item>
/// </list>
/// </summary>
public class SpecField : ViewModelBase
{
    private string _value = "";
    private bool _userEdited;
    private bool _isVaries;

    public SpecField(FieldDef def)
    {
        Def = def;
    }

    public FieldDef Def { get; }
    public string Label => Def.Label;
    public FieldRole Role => Def.Role;
    public SpecSection Section => Def.Section;

    /// <summary>Live Revit storage kind, branded by the shim collector.</summary>
    public SpecValueKind ValueKind { get; set; } = SpecValueKind.Text;

    public bool IsReadOnly { get; set; }
    public bool IsNa { get; set; }

    /// <summary>The reconciled value when symbols agree (empty for a varies/n/a field).</summary>
    public string OriginalValue { get; set; } = "";

    public bool IsVaries
    {
        get => _isVaries;
        set
        {
            if (SetProperty(ref _isVaries, value))
                OnPropertyChanged(nameof(ShowPlaceholder));
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            var v = value ?? "";
            if (_value == v) return;
            _value = v;
            _userEdited = true;
            if (_isVaries) IsVaries = false; // typing over ⟨varies⟩ resolves it
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(ShowPlaceholder));
            OnPropertyChanged(nameof(ShowUrlButton));
            OnPropertyChanged(nameof(BoolValue));
            DirtyChanged?.Invoke(this);
        }
    }

    /// <summary>True only when the user edited a writable field to a value differing from the read.</summary>
    public bool IsDirty => _userEdited && IsEditable && _value != OriginalValue;

    public bool IsEditable => !IsReadOnly && !IsNa;
    public bool IsLocked => IsReadOnly || IsNa;

    /// <summary>The grey overlay text for an empty non-normal field.</summary>
    public string Placeholder => IsNa ? "n/a" : IsVaries ? "⟨varies⟩" : "";
    public bool ShowPlaceholder => (IsNa || IsVaries) && string.IsNullOrEmpty(_value);

    /// <summary>Click-to-open glyph shows only on a URL field that currently has a value.</summary>
    public bool ShowUrlButton => Def.IsUrl && !string.IsNullOrWhiteSpace(_value);

    /// <summary>True for a Yes/No param — the form renders a checkbox instead of a text box.</summary>
    public bool IsBoolean => ValueKind == SpecValueKind.Boolean;

    /// <summary>Inverse of <see cref="IsBoolean"/>: the text editor shows for every non-boolean field.</summary>
    public bool ShowTextEditor => !IsBoolean;

    /// <summary>Checkbox state for a boolean field, persisted through <see cref="Value"/> as "1"/"0".</summary>
    public bool BoolValue
    {
        get => _value == "1";
        set => Value = value ? "1" : "0";
    }

    /// <summary>Raised whenever dirtiness may have changed; arg is this field.</summary>
    public event Action<SpecField> DirtyChanged;

    /// <summary>Initial load: set the agreed value without marking the field edited/dirty.</summary>
    public void SetInitialValue(string value)
    {
        _value = value ?? "";
        OriginalValue = _value;
        _userEdited = false;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(ShowUrlButton));
        OnPropertyChanged(nameof(BoolValue));
    }

    /// <summary>Paste a clipboard value as a user edit (marks dirty); no-op on locked fields.</summary>
    public void ApplyPaste(string value)
    {
        if (!IsEditable) return;
        Value = value;
    }

    /// <summary>Discard: revert to the read value and clear the edited flag.</summary>
    public void ResetToOriginal()
    {
        if (_value == OriginalValue && !_userEdited) return;
        _value = OriginalValue;
        _userEdited = false;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(ShowUrlButton));
        OnPropertyChanged(nameof(BoolValue));
        DirtyChanged?.Invoke(this);
    }

    /// <summary>After a successful write: the current value becomes the new baseline.</summary>
    public void MarkSaved()
    {
        OriginalValue = _value;
        _userEdited = false;
        OnPropertyChanged(nameof(IsDirty));
        DirtyChanged?.Invoke(this);
    }
}
