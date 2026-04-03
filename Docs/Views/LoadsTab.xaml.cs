using System.Windows.Controls;
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
        if (DataContext is not LoadsViewModel vm) return;

        vm.SelectedSortColumn = e.Column.Header switch
        {
            "Circuit" => "CircuitNumber",
            "Load" => "LoadName",
            "Wattage" => "TotalWatts",
            _ => "CircuitNumber"
        };

        // Next direction: None→Asc, Asc→Desc, Desc→Asc
        vm.SortDescending = e.Column.SortDirection == System.ComponentModel.ListSortDirection.Ascending;
    }
}
