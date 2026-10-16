#nullable disable
using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TurboSuite.Driver.Models;

namespace TurboSuite.Driver.Views
{
    /// <summary>
    /// Driver Match comparison panel (the "Driver Match" tab of the TurboRPS window). Read-only;
    /// binds to <see cref="TurboSuite.Driver.ViewModels.DriverMatchViewModel"/>.
    /// </summary>
    public partial class DriverMatchView : UserControl
    {
        public DriverMatchView()
        {
            InitializeComponent();
        }
    }

    /// <summary>Verdict → glyph for the status column.</summary>
    public class VerdictToGlyphConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is MatchVerdict v
                ? v switch
                {
                    MatchVerdict.Pass => "✓", // ✓
                    MatchVerdict.Fail => "✗", // ✗
                    MatchVerdict.Warn => "⚠", // ⚠
                    _ => "ⓘ"                   // ⓘ
                }
                : string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Verdict → glyph foreground colour.</summary>
    public class VerdictToForegroundConverter : IValueConverter
    {
        private static readonly Brush Pass = Freeze("#1E7E34");
        private static readonly Brush Fail = Freeze("#C0392B");
        private static readonly Brush Warn = Freeze("#B8860B");
        private static readonly Brush Info = Freeze("#6C757D");

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is MatchVerdict v
                ? v switch
                {
                    MatchVerdict.Pass => Pass,
                    MatchVerdict.Fail => Fail,
                    MatchVerdict.Warn => Warn,
                    _ => Info
                }
                : Info;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static Brush Freeze(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }

    /// <summary>Verdict → subtle row tint (only Fail and Warn are tinted).</summary>
    public class VerdictToRowBrushConverter : IValueConverter
    {
        private static readonly Brush FailTint = Freeze("#FDECEA");
        private static readonly Brush WarnTint = Freeze("#FFF8E1");

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is MatchVerdict v
                ? v switch
                {
                    MatchVerdict.Fail => FailTint,
                    MatchVerdict.Warn => WarnTint,
                    _ => Brushes.Transparent
                }
                : Brushes.Transparent;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static Brush Freeze(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }
}
