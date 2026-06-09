#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Autodesk.Revit.DB;
using TurboSuite.Setup.Models;
using TurboSuite.Shared.ViewModels;

namespace TurboSuite.Setup.ViewModels;

/// <summary>
/// Stage 2: map each generated host view to a linked view (or leave it unmapped). Floor rows
/// offer linked Floor Plans; RCP rows offer linked Ceiling Plans. Each dropdown includes a
/// "(none)" sentinel so a view can be created with no link override.
/// </summary>
public class TurboSetupViewMappingViewModel : ViewModelBase
{
    /// <summary>A selectable linked view (or the "(none)" sentinel when <see cref="ViewId"/> is invalid).</summary>
    public class LinkedViewOption
    {
        public LinkedViewOption(ElementId viewId, string displayName)
        {
            ViewId = viewId;
            DisplayName = displayName;
        }

        public ElementId ViewId { get; }
        public string DisplayName { get; }
        public bool IsNone => ViewId == null || ViewId == ElementId.InvalidElementId;
        public override string ToString() => DisplayName;
    }

    public class ViewMappingRow : ViewModelBase
    {
        private LinkedViewOption _selectedLinkedView;

        public string ViewName { get; set; }
        public ElementId SourceLevelId { get; set; }
        public ObservableCollection<LinkedViewOption> AvailableLinkedViews { get; } =
            new ObservableCollection<LinkedViewOption>();

        public LinkedViewOption SelectedLinkedView
        {
            get => _selectedLinkedView;
            set => SetProperty(ref _selectedLinkedView, value);
        }
    }

    public ObservableCollection<ViewMappingRow> Rows { get; } = new ObservableCollection<ViewMappingRow>();

    public bool Confirmed { get; private set; }

    public ICommand OkCommand { get; }

    public event Action CloseRequested;

    public TurboSetupViewMappingViewModel(IEnumerable<ViewMappingRow> rows)
    {
        foreach (var row in rows)
            Rows.Add(row);

        OkCommand = new RelayCommand(ExecuteOk);
    }

    private void ExecuteOk()
    {
        Confirmed = true;
        CloseRequested?.Invoke();
    }

    /// <summary>Resolved mapping: planned view name → linked view id (invalid id = unmapped).</summary>
    public Dictionary<string, ElementId> GetMapping()
    {
        var map = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
        {
            var sel = row.SelectedLinkedView;
            map[row.ViewName] = (sel == null || sel.IsNone) ? ElementId.InvalidElementId : sel.ViewId;
        }
        return map;
    }
}
