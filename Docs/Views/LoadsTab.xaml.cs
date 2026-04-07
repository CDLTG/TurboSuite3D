using System;
using System.Collections;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using TurboSuite.Docs.Models;
using TurboSuite.Docs.ViewModels;

namespace TurboSuite.Docs.Views;

public partial class LoadsTab : UserControl
{
    public LoadsTab()
    {
        InitializeComponent();
    }

    private void DataGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not LoadsViewModel vm) return;

        // Toggle direction: None/Desc → Asc, Asc → Desc
        var newDirection = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        e.Column.SortDirection = newDirection;

        // Clear other columns' sort arrows
        if (sender is DataGrid dg)
        {
            foreach (var col in dg.Columns)
                if (col != e.Column) col.SortDirection = null;
        }

        vm.SelectedSortColumn = e.Column.Header switch
        {
            "Circuit" => "CircuitNumber",
            "Load" => "LoadName",
            "Wattage" => "TotalWatts",
            _ => "CircuitNumber"
        };
        vm.SortDescending = newDirection == ListSortDirection.Descending;

        // Apply custom sort that always pins <...> to the bottom
        var view = CollectionViewSource.GetDefaultView(vm.Circuits);
        if (view is ListCollectionView lcv)
            lcv.CustomSort = new LoadsCircuitComparer(vm.SelectedSortColumn, vm.SortDescending);
    }

    private class LoadsCircuitComparer : IComparer
    {
        private readonly string _column;
        private readonly bool _descending;

        public LoadsCircuitComparer(string column, bool descending)
        {
            _column = column;
            _descending = descending;
        }

        public int Compare(object? x, object? y)
        {
            if (x is not LoadsCircuitModel a || y is not LoadsCircuitModel b) return 0;

            // <...> always sorts to the bottom regardless of direction
            bool aPlaceholder = a.CircuitNumber == "<...>";
            bool bPlaceholder = b.CircuitNumber == "<...>";
            if (aPlaceholder && bPlaceholder) return 0;
            if (aPlaceholder) return 1;
            if (bPlaceholder) return -1;

            int result = _column switch
            {
                "LoadName" => string.Compare(a.LoadName, b.LoadName, StringComparison.OrdinalIgnoreCase),
                "TotalWatts" => a.ApparentLoadVA.CompareTo(b.ApparentLoadVA),
                _ => string.Compare(a.CircuitNumber, b.CircuitNumber, StringComparison.OrdinalIgnoreCase),
            };

            return _descending ? -result : result;
        }
    }
}
