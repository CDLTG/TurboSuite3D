#nullable disable
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using TurboSuite.Name.Services;
using RevitColor = Autodesk.Revit.DB.Color;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using MediaBrush = System.Windows.Media.SolidColorBrush;

namespace TurboSuite.Name.Views;

/// <summary>
/// Per-layer Line Graphics editor for TurboName's folded-in VG → Imported Categories table (TurboName-12) —
/// a faithful stand-in for Revit's native "Line Graphics" popup: Pattern / Color / Weight, with Clear Overrides
/// and OK/Cancel. Seeded from the layer's current <see cref="OverrideGraphicSettings"/> (cloned, so surface /
/// halftone overrides survive), it composes a new override on OK and exposes it via <see cref="Result"/>; the
/// caller writes it through the shared external event. All value-object work — no Revit API context needed here
/// (spike-confirmed). Color is picked with the native Windows <see cref="System.Windows.Forms.ColorDialog"/>.
/// </summary>
public partial class LineGraphicsDialog : Window
{
    private const string NoOverride = "<No Override>";

    private readonly OverrideGraphicSettings _current;
    private RevitColor _color;      // working color; _hasColor gates whether it's an override
    private bool _hasColor;

    /// <summary>The composed override the caller applies. Null until OK / Clear Overrides.</summary>
    public OverrideGraphicSettings Result { get; private set; }

    public LineGraphicsDialog(string layerName, OverrideGraphicSettings current,
        IReadOnlyList<LinePatternOption> patternOptions)
    {
        InitializeComponent();
        _current = current ?? new OverrideGraphicSettings();
        Title = $"Line Graphics — {layerName}";

        // Pattern roster + seed from the current pattern id (falls back to <No Override>).
        PatternCombo.ItemsSource = patternOptions;
        var patId = _current.ProjectionLinePatternId ?? ElementId.InvalidElementId;
        PatternCombo.SelectedItem =
            patternOptions.FirstOrDefault(o => o.Id == patId) ?? patternOptions.FirstOrDefault();

        // Weight roster: <No Override> + 1..16, seeded from the current weight.
        var weights = new List<object> { NoOverride };
        for (int w = 1; w <= 16; w++) weights.Add(w);
        WeightCombo.ItemsSource = weights;
        int curWeight = _current.ProjectionLineWeight;
        WeightCombo.SelectedItem = curWeight >= 1 && curWeight <= 16 ? (object)curWeight : NoOverride;

        // Color: seed from the current override if it carries a valid one.
        var cc = _current.ProjectionLineColor;
        if (cc != null && cc.IsValid) { _color = cc; _hasColor = true; }
        UpdateColorDisplay();
    }

    private void UpdateColorDisplay()
    {
        if (_hasColor)
        {
            ColorSwatch.Background = new MediaBrush(MediaColor.FromRgb(_color.Red, _color.Green, _color.Blue));
            ColorLabel.Text = $"RGB {_color.Red}-{_color.Green}-{_color.Blue}";
        }
        else
        {
            ColorSwatch.Background = System.Windows.Media.Brushes.Transparent;
            ColorLabel.Text = NoOverride;
        }
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        if (_hasColor) dlg.Color = DrawingColor.FromArgb(_color.Red, _color.Green, _color.Blue);
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _color = new RevitColor(dlg.Color.R, dlg.Color.G, dlg.Color.B);
            _hasColor = true;
            UpdateColorDisplay();
        }
    }

    // Clone the current override (keeps surface/halftone), then write the three Lines fields — set or clear.
    private OverrideGraphicSettings Compose(bool clear)
    {
        var ogs = new OverrideGraphicSettings(_current);

        if (clear || WeightCombo.SelectedItem is not int weight)
            ogs.SetProjectionLineWeight(-1);
        else
            ogs.SetProjectionLineWeight(weight);

        if (clear || !_hasColor)
            ogs.SetProjectionLineColor(RevitColor.InvalidColorValue);
        else
            ogs.SetProjectionLineColor(_color);

        var patId = clear ? ElementId.InvalidElementId
            : (PatternCombo.SelectedItem as LinePatternOption)?.Id ?? ElementId.InvalidElementId;
        ogs.SetProjectionLinePatternId(patId);

        return ogs;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Result = Compose(clear: false);
        DialogResult = true;
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        Result = Compose(clear: true);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
