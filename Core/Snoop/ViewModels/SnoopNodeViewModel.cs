#nullable disable
using System.Collections.ObjectModel;
using TurboSuite.Shared.ViewModels;
using TurboSuite.Snoop.Models;

namespace TurboSuite.Snoop.ViewModels;

/// <summary>
/// Bindable wrapper over a pure <see cref="SnoopNode"/> for the TurboSnoop TreeView, adding the WPF concerns
/// the model omits (expand state, the leaf-bullet flag). Native TreeView expanders supply the hierarchy
/// affordance, so there is no glyph column.
/// </summary>
public sealed class SnoopNodeViewModel : ViewModelBase
{
    private bool _isExpanded = true;

    private SnoopNodeViewModel(string label, SnoopNodeKind kind)
    {
        Label = label;
        Kind = kind;
    }

    public string Label { get; }

    public SnoopNodeKind Kind { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public ObservableCollection<SnoopNodeViewModel> Children { get; } = new();

    /// <summary>True for a leaf VG checkbox (a Category/Subcategory with no children) — the exact row the
    /// user must find in the V/G dialog. Drives a leading bullet. Info leaves (e.g. "(none)") are excluded.</summary>
    public bool IsLeafCheckbox => Children.Count == 0 && Kind != SnoopNodeKind.Info;

    /// <summary>Walks the model tree into bindable VMs, expanded by default.</summary>
    public static SnoopNodeViewModel BuildTree(SnoopNode node)
    {
        var vm = new SnoopNodeViewModel(node.Label, node.Kind);
        foreach (SnoopNode child in node.Children)
            vm.Children.Add(BuildTree(child));
        return vm;
    }
}
