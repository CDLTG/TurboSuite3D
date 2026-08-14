#nullable enable
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TurboSuite.Dali.Views
{
    /// <summary>
    /// The standalone <b>TurboDALI</b> modeless window — the DALI loop-declaration UI plus the addressing
    /// surface (Write addresses, numbering lock, REVIEW list) and the live zone color overlay, dressed in the
    /// DMX chrome (blue header + roll-up, footer bar). DataContext is a <c>DaliMainViewModel</c> (which wraps
    /// the loop-declaration <c>DaliTab</c>), collected + shown by <c>DaliCommand</c>.
    /// </summary>
    public partial class TurboDaliWindow : Window
    {
        public TurboDaliWindow()
        {
            InitializeComponent();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }

    /// <summary>bool → Visibility, inverted (true ⇒ Collapsed) — the placeholder/content flip. Local twin of
    /// the TurboZones window's converter; TurboSuiteStyles only ships the non-inverted BoolToVisibility.</summary>
    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility v && v != Visibility.Visible;
    }
}
