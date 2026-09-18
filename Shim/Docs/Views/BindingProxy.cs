using System.Windows;

namespace TurboSuite.Docs.Views;

/// <summary>
/// A DataContext-carrying <see cref="Freezable"/> so a <c>DataGridColumn.Header</c> — which lives
/// outside the visual tree and inherits no DataContext — can bind to a viewmodel property. Declared as
/// a resource with <c>Data="{Binding}"</c>; columns then bind <c>Data.SomeProperty</c> against it.
/// Used by the Cut Sheets tab to swap column headers between Fixture and Control modes.
/// </summary>
public sealed class BindingProxy : Freezable
{
    protected override Freezable CreateInstanceCore() => new BindingProxy();

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy),
            new UIPropertyMetadata(null));
}
