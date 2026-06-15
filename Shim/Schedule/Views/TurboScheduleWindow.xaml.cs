#nullable disable
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using TurboSuite.Schedule.ViewModels;

namespace TurboSuite.Schedule.Views
{
    public partial class TurboScheduleWindow : Window
    {
        public TurboScheduleWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        // The window opens with SizeToContent=Height so its initial height fits the form exactly (no
        // dead band). Once laid out, lock that as the minimum height and switch to manual sizing so the
        // user can grow it vertically — the stretch (*) body/column rows then absorb the extra height
        // (acceptable whitespace at the bottom of the columns) while Notes + the action bar stay anchored.
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            MinHeight = ActualHeight;
            SizeToContent = SizeToContent.Manual;
        }

        // Close-time guard: prompt when dirty. Yes saves (async) and keeps the window open so the
        // user closes again once clean; No discards by closing; Cancel keeps the window open.
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (DataContext is not ScheduleMainViewModel vm || !vm.HasUnsavedChanges)
                return;

            var choice = MessageBox.Show(
                $"Unsaved changes on {vm.DirtyCount} type(s).\n\nSave them before closing?",
                "TurboSchedule",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (choice == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
            }
            else if (choice == MessageBoxResult.Yes)
            {
                e.Cancel = true; // let the async save finish; closing again will be clean
                if (vm.SaveCommand.CanExecute(null))
                    vm.SaveCommand.Execute(null);
            }
            // No → fall through and close, discarding edits.
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
}
