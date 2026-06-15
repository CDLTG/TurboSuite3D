#nullable disable
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using TurboSuite.Schedule.Models;
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

            // WindowStartupLocation="CenterScreen" positions the window before SizeToContent="Height"
            // has resolved the final height, so it lands too high. Re-center against the work area now
            // that ActualWidth/Height are known.
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Top + (area.Height - ActualHeight) / 2;
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

        // The ↗ glyph on a URL field opens the value in the default browser. Bare values (no scheme)
        // get https:// prepended; a malformed URL is swallowed rather than thrown at the user.
        private void UrlGlyph_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is not SpecField field)
                return;

            var url = field.Value?.Trim();
            if (string.IsNullOrEmpty(url))
                return;
            if (!url.Contains("://"))
                url = "https://" + url;

            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* malformed/unsupported URL — ignore */ }
        }
    }
}
